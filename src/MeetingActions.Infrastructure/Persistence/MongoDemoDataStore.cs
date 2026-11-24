using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MongoDB.Driver;

namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Seeds or replaces all demo collections as one explicit operational workflow.</summary>
public sealed class MongoDemoDataStore(MongoContext context) : IDemoDataStore
{
    /// <summary>Returns whether the store has no existing demo users or meetings.</summary>
    public async Task<bool> IsEmptyAsync(CancellationToken cancellationToken = default)
    {
        var usersExist = await context.Users.Find(Builders<User>.Filter.Empty).Limit(1).AnyAsync(cancellationToken);
        var meetingsExist = await context.Meetings.Find(Builders<MongoMeetingDocument>.Filter.Empty)
            .Limit(1).AnyAsync(cancellationToken);
        return !usersExist && !meetingsExist;
    }

    /// <summary>Seeds the initial users, meeting, and extracted items when the store is empty.</summary>
    public async Task SeedAsync(
        IReadOnlyCollection<User> users,
        Meeting meeting,
        CancellationToken cancellationToken = default)
    {
        if (!await IsEmptyAsync(cancellationToken))
        {
            return;
        }

        await context.Users.InsertManyAsync(users, cancellationToken: cancellationToken);
        await context.Meetings.InsertOneAsync(
            MongoMeetingMapper.ToDocument(meeting), cancellationToken: cancellationToken);
        if (meeting.Items.Count > 0)
        {
            await context.ActionItems.InsertManyAsync(meeting.Items, cancellationToken: cancellationToken);
        }
    }

    /// <summary>Drops and recreates collections, then inserts the supplied demo scenario.</summary>
    public async Task ResetAsync(
        IReadOnlyCollection<User> users,
        Meeting meeting,
        CancellationToken cancellationToken = default)
    {
        await context.DropAndRecreateAsync(cancellationToken);
        await context.Users.InsertManyAsync(users, cancellationToken: cancellationToken);
        await context.Meetings.InsertOneAsync(
            MongoMeetingMapper.ToDocument(meeting), cancellationToken: cancellationToken);
        if (meeting.Items.Count > 0)
        {
            await context.ActionItems.InsertManyAsync(meeting.Items, cancellationToken: cancellationToken);
        }
    }
}
