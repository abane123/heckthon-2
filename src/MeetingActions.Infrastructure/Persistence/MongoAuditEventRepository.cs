using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MongoDB.Driver;

namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Stores immutable audit records separately from the action items they describe.</summary>
public sealed class MongoAuditEventRepository(MongoContext context) : IAuditEventRepository
{
    /// <summary>Records one audit event.</summary>
    public Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) =>
        context.AuditEvents.InsertOneAsync(auditEvent, cancellationToken: cancellationToken);

    /// <summary>Records multiple audit events.</summary>
    public Task AddManyAsync(IReadOnlyCollection<AuditEvent> auditEvents, CancellationToken cancellationToken = default) =>
        auditEvents.Count == 0
            ? Task.CompletedTask
            : context.AuditEvents.InsertManyAsync(auditEvents, cancellationToken: cancellationToken);

    /// <summary>Gets an action item's audit events in chronological order.</summary>
    public async Task<IReadOnlyList<AuditEvent>> GetByActionItemAsync(
        Guid actionItemId,
        CancellationToken cancellationToken = default) =>
        await context.AuditEvents.Find(auditEvent => auditEvent.ActionItemId == actionItemId)
            .SortBy(auditEvent => auditEvent.CreatedAt).ToListAsync(cancellationToken);
}
