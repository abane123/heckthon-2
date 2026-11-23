using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Implements meeting and action-item persistence using EF Core.</summary>
public sealed class MeetingRepository(MeetingActionsDbContext dbContext) : IMeetingRepository
{
    /// <summary>Gets meetings with their action items, newest first.</summary>
    public async Task<IReadOnlyList<Meeting>> GetMeetingsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Meetings.Include(meeting => meeting.Items).OrderByDescending(meeting => meeting.MeetingDate).ToListAsync(cancellationToken);

    /// <summary>Gets a meeting and its action items by identifier.</summary>
    public Task<Meeting?> GetMeetingAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.Meetings.Include(meeting => meeting.Items).FirstOrDefaultAsync(meeting => meeting.Id == id, cancellationToken);

    /// <summary>Gets the action items belonging to a meeting.</summary>
    public async Task<IReadOnlyList<ActionItem>> GetItemsAsync(Guid meetingId, CancellationToken cancellationToken = default) =>
        await dbContext.ActionItems.Where(item => item.MeetingId == meetingId).ToListAsync(cancellationToken);

    /// <summary>Gets an action item by identifier.</summary>
    public Task<ActionItem?> GetItemAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.ActionItems.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

    /// <summary>Gets every action item.</summary>
    public async Task<IReadOnlyList<ActionItem>> GetAllItemsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.ActionItems.ToListAsync(cancellationToken);

    /// <summary>Adds a meeting graph to the current unit of work.</summary>
    public async Task AddMeetingAsync(Meeting meeting, CancellationToken cancellationToken = default)
    {
        await dbContext.Meetings.AddAsync(meeting, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Persists pending context changes.</summary>
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => dbContext.SaveChangesAsync(cancellationToken);

    /// <summary>Gets audit entries for an action in chronological order.</summary>
    public async Task<IReadOnlyList<AuditEvent>> GetAuditEventsAsync(Guid actionItemId, CancellationToken cancellationToken = default) =>
        await dbContext.AuditEvents.Where(entry => entry.ActionItemId == actionItemId).OrderBy(entry => entry.CreatedAt).ToListAsync(cancellationToken);
}
