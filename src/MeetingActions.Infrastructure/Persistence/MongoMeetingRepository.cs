using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MongoDB.Driver;

namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Persists meeting metadata in MongoDB and resolves action items from their separate collection.</summary>
public sealed class MongoMeetingRepository(MongoContext context, IActionItemRepository actionItems) : IMeetingRepository
{
    /// <summary>Gets all meetings with their separately stored action items.</summary>
    public async Task<IReadOnlyList<Meeting>> GetMeetingsAsync(CancellationToken cancellationToken = default)
    {
        var documents = await context.Meetings.Find(Builders<MongoMeetingDocument>.Filter.Empty)
            .SortByDescending(meeting => meeting.MeetingDate)
            .ToListAsync(cancellationToken);
        var allItems = await actionItems.GetAllItemsAsync(cancellationToken);
        var itemsByMeeting = allItems.GroupBy(item => item.MeetingId).ToDictionary(group => group.Key, group => group.ToList());
        return documents.Select(document => MongoMeetingMapper.ToDomain(
            document, itemsByMeeting.GetValueOrDefault(document.Id) ?? [])).ToArray();
    }

    /// <summary>Gets one meeting with its separately stored action items.</summary>
    public async Task<Meeting?> GetMeetingAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var document = await context.Meetings.Find(meeting => meeting.Id == id).FirstOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return null;
        }

        var items = await actionItems.GetItemsAsync(id, cancellationToken);
        return MongoMeetingMapper.ToDomain(document, items);
    }

    /// <summary>Inserts meeting metadata and its items into their respective collections.</summary>
    public async Task AddMeetingAsync(Meeting meeting, CancellationToken cancellationToken = default)
    {
        await context.Meetings.InsertOneAsync(
            MongoMeetingMapper.ToDocument(meeting), cancellationToken: cancellationToken);
        try
        {
            await actionItems.AddManyAsync(meeting.Items, cancellationToken);
        }
        catch
        {
            // Remove the meeting document if its separate action-item insert fails.
            await context.Meetings.DeleteOneAsync(entry => entry.Id == meeting.Id, cancellationToken);
            throw;
        }
    }

    /// <summary>Updates meeting metadata without embedding its action items.</summary>
    public async Task UpdateMeetingAsync(Meeting meeting, CancellationToken cancellationToken = default)
    {
        var update = Builders<MongoMeetingDocument>.Update
            .Set(document => document.Title, meeting.Title)
            .Set(document => document.SeriesId, meeting.Title)
            .Set(document => document.MeetingDate, meeting.MeetingDate)
            .Set(document => document.Transcript, meeting.Transcript)
            .Set(document => document.Attendees, meeting.Attendees);
        await context.Meetings.UpdateOneAsync(
            document => document.Id == meeting.Id, update, cancellationToken: cancellationToken);
    }

    /// <summary>Gets open items from earlier meetings sharing this meeting's title.</summary>
    public async Task<IReadOnlyList<ActionItem>> GetNextAgendaItemsAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default)
    {
        var current = await context.Meetings.Find(meeting => meeting.Id == meetingId)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return [];
        }

        var priorMeetingIds = await context.Meetings.Find(meeting =>
                meeting.Id != meetingId
                && meeting.SeriesId == current.SeriesId
                && meeting.MeetingDate < current.MeetingDate)
            .Project(meeting => meeting.Id)
            .ToListAsync(cancellationToken);
        if (priorMeetingIds.Count == 0)
        {
            return [];
        }

        var filter = Builders<ActionItem>.Filter.In(item => item.MeetingId, priorMeetingIds)
            & Builders<ActionItem>.Filter.Nin(item => item.Status, [ActionStatus.Done, ActionStatus.Rejected]);
        return await context.ActionItems.Find(filter).ToListAsync(cancellationToken);
    }
}

/// <summary>Maps the API meeting entity to a collection document with its existing title as series key.</summary>
internal static class MongoMeetingMapper
{
    /// <summary>Creates a MongoDB meeting document without embedded action items.</summary>
    public static MongoMeetingDocument ToDocument(Meeting meeting) =>
        new()
        {
            Id = meeting.Id,
            SeriesId = meeting.Title,
            Title = meeting.Title,
            MeetingDate = meeting.MeetingDate,
            Transcript = meeting.Transcript,
            Attendees = meeting.Attendees,
            Decisions = meeting.Decisions,
            Analysis = meeting.Analysis
        };

    /// <summary>Creates an API domain meeting and attaches separately queried action items.</summary>
    public static Meeting ToDomain(MongoMeetingDocument document, IReadOnlyList<ActionItem> items) =>
        new()
        {
            Id = document.Id,
            Title = document.Title,
            MeetingDate = document.MeetingDate,
            Transcript = document.Transcript,
            Attendees = document.Attendees,
            Items = items.ToList(),
            Decisions = document.Decisions,
            Analysis = document.Analysis
        };
}
