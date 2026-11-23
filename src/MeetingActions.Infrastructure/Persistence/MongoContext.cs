using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Options;

namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Owns the MongoDB client, database, collections, and idempotent startup indexes.</summary>
public sealed class MongoContext
{
    /// <summary>Initializes collection handles against the configured MongoDB database.</summary>
    public MongoContext(IMongoClient client, IOptions<MongoOptions> options)
    {
        Client = client;
        Database = client.GetDatabase(options.Value.DatabaseName);
        Meetings = Database.GetCollection<MongoMeetingDocument>("meetings");
        ActionItems = Database.GetCollection<MeetingActions.Core.Domain.ActionItem>("actionItems");
        Users = Database.GetCollection<MeetingActions.Core.Domain.User>("users");
        FollowUps = Database.GetCollection<MeetingActions.Core.Domain.FollowUp>("followUps");
        AuditEvents = Database.GetCollection<MeetingActions.Core.Domain.AuditEvent>("auditEvents");
    }

    /// <summary>Gets the shared MongoDB client.</summary>
    public IMongoClient Client { get; }

    /// <summary>Gets the configured MongoDB database.</summary>
    public IMongoDatabase Database { get; }

    /// <summary>Gets the meetings collection handle.</summary>
    internal IMongoCollection<MongoMeetingDocument> Meetings { get; }

    /// <summary>Gets the action-items collection handle.</summary>
    internal IMongoCollection<MeetingActions.Core.Domain.ActionItem> ActionItems { get; }

    /// <summary>Gets the users collection handle.</summary>
    internal IMongoCollection<MeetingActions.Core.Domain.User> Users { get; }

    /// <summary>Gets the follow-ups collection handle.</summary>
    internal IMongoCollection<MeetingActions.Core.Domain.FollowUp> FollowUps { get; }

    /// <summary>Gets the audit-events collection handle.</summary>
    internal IMongoCollection<MeetingActions.Core.Domain.AuditEvent> AuditEvents { get; }

    /// <summary>Creates required indexes; equivalent index definitions make repeated startup safe.</summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        await Meetings.Indexes.CreateOneAsync(
            new CreateIndexModel<MongoMeetingDocument>(
                Builders<MongoMeetingDocument>.IndexKeys
                    .Ascending(meeting => meeting.SeriesId)
                    .Descending(meeting => meeting.MeetingDate),
                new CreateIndexOptions { Name = "ix_meetings_seriesId_meetingDate" }),
            cancellationToken: cancellationToken);

        // Meeting lookups and dashboard filtering use separate indexed fields to avoid collection scans.
        await ActionItems.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<MeetingActions.Core.Domain.ActionItem>(
                Builders<MeetingActions.Core.Domain.ActionItem>.IndexKeys.Ascending(item => item.MeetingId),
                new CreateIndexOptions { Name = "ix_actionItems_meetingId" }),
            new CreateIndexModel<MeetingActions.Core.Domain.ActionItem>(
                Builders<MeetingActions.Core.Domain.ActionItem>.IndexKeys
                    .Ascending(item => item.Status)
                    .Ascending(item => item.DueDate),
                new CreateIndexOptions { Name = "ix_actionItems_status_dueDate" }),
            new CreateIndexModel<MeetingActions.Core.Domain.ActionItem>(
                Builders<MeetingActions.Core.Domain.ActionItem>.IndexKeys
                    .Ascending(item => item.OwnerId)
                    .Ascending(item => item.Status),
                new CreateIndexOptions { Name = "ix_actionItems_ownerId_status" })
        ], cancellationToken);

        // The compound keys support chronological history and same-day duplicate-nudge checks.
        await AuditEvents.Indexes.CreateOneAsync(
            new CreateIndexModel<MeetingActions.Core.Domain.AuditEvent>(
                Builders<MeetingActions.Core.Domain.AuditEvent>.IndexKeys
                    .Ascending(auditEvent => auditEvent.ActionItemId)
                    .Ascending(auditEvent => auditEvent.CreatedAt),
                new CreateIndexOptions { Name = "ix_auditEvents_actionItemId_createdAt" }),
            cancellationToken: cancellationToken);
        await FollowUps.Indexes.CreateOneAsync(
            new CreateIndexModel<MeetingActions.Core.Domain.FollowUp>(
                Builders<MeetingActions.Core.Domain.FollowUp>.IndexKeys
                    .Ascending(followUp => followUp.ActionItemId)
                    .Ascending(followUp => followUp.SentAt),
                new CreateIndexOptions { Name = "ix_followUps_actionItemId_sentAt" }),
            cancellationToken: cancellationToken);
        await Users.Indexes.CreateOneAsync(
            new CreateIndexModel<MeetingActions.Core.Domain.User>(
                Builders<MeetingActions.Core.Domain.User>.IndexKeys.Ascending(user => user.Name),
                new CreateIndexOptions { Name = "ux_users_name", Unique = true }),
            cancellationToken: cancellationToken);
    }

    /// <summary>Drops all domain collections and recreates their indexes for a demo reset.</summary>
    public async Task DropAndRecreateAsync(CancellationToken cancellationToken = default)
    {
        await Database.DropCollectionAsync("meetings", cancellationToken);
        await Database.DropCollectionAsync("actionItems", cancellationToken);
        await Database.DropCollectionAsync("users", cancellationToken);
        await Database.DropCollectionAsync("followUps", cancellationToken);
        await Database.DropCollectionAsync("auditEvents", cancellationToken);
        await EnsureIndexesAsync(cancellationToken);
    }

    /// <summary>Sends a ping command to verify the configured MongoDB endpoint.</summary>
    public async Task PingAsync(CancellationToken cancellationToken = default) =>
        await Database.RunCommandAsync<BsonDocument>(
            new BsonDocument("ping", 1),
            cancellationToken: cancellationToken);
}
