using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using Microsoft.Extensions.Logging;

namespace MeetingActions.Jobs;

/// <summary>Runs deadline reminders, overdue checks, and escalations for open action items.</summary>
public sealed class FollowUpService(
    IActionItemRepository actionItemRepository,
    IFollowUpRepository followUpRepository,
    IAuditEventRepository auditEventRepository,
    ITaskProvider taskProvider,
    INotifier notifier,
    IClock clock,
    ActionItemStateMachine stateMachine,
    ILogger<FollowUpService> logger)
{
    private static readonly string[] QuickReplies = ["Done", "PushToFriday", "Blocked", "Reassign"];
    private static readonly ActionStatus[] FollowUpStatuses =
    [
        ActionStatus.Created,
        ActionStatus.InProgress,
        ActionStatus.Overdue,
        ActionStatus.Escalated
    ];

    /// <summary>Processes action items once and returns the reminders sent during this pass.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        // Include tomorrow's due items for reminders and exclude irrelevant statuses in the repository query.
        var dueThroughTomorrow = now.Date.AddDays(2).AddTicks(-1);
        var items = await actionItemRepository.GetDueOnOrBeforeAsync(
            dueThroughTomorrow, FollowUpStatuses, cancellationToken);
        var remindersSent = 0;
        foreach (var item in items)
        {
            if (item.DueDate is null || string.IsNullOrWhiteSpace(item.ExternalTicketId))
            {
                continue;
            }

            var expectedVersion = item.Version;
            var changed = false;
            var newFollowUps = new List<FollowUp>();
            var newAuditEvents = new List<AuditEvent>();
            var recipient = item.OwnerName ?? "Organizer";
            if (item.DueDate.Value.Date == now.Date.AddDays(1)
                && item.Status is ActionStatus.Created or ActionStatus.InProgress)
            {
                await RecordNotificationAsync(
                    item, recipient, "Reminder", "This action is due tomorrow.", "reminder", now,
                    null, newFollowUps, newAuditEvents, cancellationToken);
                remindersSent++;
            }

            if (item.DueDate.Value.Date > now.Date)
            {
                await PersistRecordsAsync(newFollowUps, newAuditEvents, cancellationToken);
                continue;
            }

            var externalStatus = await taskProvider.GetStatusAsync(item.ExternalTicketId, cancellationToken);
            newFollowUps.Add(new FollowUp
            {
                ActionItemId = item.Id,
                SentAt = now,
                Channel = "ticket-status-check",
                Response = externalStatus ?? "Unknown"
            });
            newAuditEvents.Add(CreateAudit(item, $"Ticket status checked: {externalStatus ?? "Unknown"}", now));
            if (string.Equals(externalStatus, "Done", StringComparison.OrdinalIgnoreCase)
                || string.Equals(externalStatus, "Closed", StringComparison.OrdinalIgnoreCase))
            {
                TransitionAndRecord(item, ActionStatus.Done, "External ticket completed", now, newFollowUps, newAuditEvents);
                changed = true;
            }
            else
            {
                if (item.Status is ActionStatus.Created or ActionStatus.InProgress)
                {
                    if (item.Status == ActionStatus.Created)
                    {
                        TransitionAndRecord(item, ActionStatus.InProgress, "Work started for deadline tracking", now,
                            newFollowUps, newAuditEvents);
                    }

                    TransitionAndRecord(item, ActionStatus.Overdue, "Action became overdue", now,
                        newFollowUps, newAuditEvents);
                    changed = true;
                }

                if (item.Status is ActionStatus.Overdue or ActionStatus.Escalated)
                {
                    var alreadyNudged = await followUpRepository.ExistsOnDateAsync(
                        item.Id, "overdue", now.Date, cancellationToken);
                    if (!alreadyNudged)
                    {
                        item.SlipCount++;
                        changed = true;
                        await RecordNotificationAsync(
                            item, recipient, "Overdue", $"Action slipped {item.SlipCount} time(s).", "overdue",
                            now, QuickReplies, newFollowUps, newAuditEvents, cancellationToken);
                        if (item.SlipCount >= 2 && item.Status == ActionStatus.Overdue)
                        {
                            TransitionAndRecord(item, ActionStatus.Escalated, "Escalated after two slips", now,
                                newFollowUps, newAuditEvents);
                            await RecordNotificationAsync(
                                item, recipient, "Escalation", "This action has been escalated after repeated slips.",
                                "escalation", now, QuickReplies, newFollowUps, newAuditEvents, cancellationToken);
                        }
                    }
                }
            }

            if (changed && !await actionItemRepository.TryUpdateAsync(item, expectedVersion, cancellationToken))
            {
                logger.LogWarning("Action item {ActionItemId} changed while follow-up was processing it.", item.Id);
            }

            await PersistRecordsAsync(newFollowUps, newAuditEvents, cancellationToken);
        }

        logger.LogInformation("Follow-up run completed at {Time}", now);
        return remindersSent;
    }

    /// <summary>Sends a notification and buffers its follow-up and audit records.</summary>
    private async Task RecordNotificationAsync(
        ActionItem item,
        string recipient,
        string label,
        string message,
        string channel,
        DateTime sentAt,
        IReadOnlyList<string>? quickReplies,
        ICollection<FollowUp> followUps,
        ICollection<AuditEvent> auditEvents,
        CancellationToken cancellationToken)
    {
        await notifier.SendAsync(recipient, message, quickReplies, cancellationToken);
        followUps.Add(new FollowUp { ActionItemId = item.Id, SentAt = sentAt, Channel = channel });
        auditEvents.Add(CreateAudit(item, label, sentAt));
    }

    /// <summary>Transitions an item and buffers matching workflow records.</summary>
    private void TransitionAndRecord(
        ActionItem item,
        ActionStatus next,
        string eventType,
        DateTime createdAt,
        ICollection<FollowUp> followUps,
        ICollection<AuditEvent> auditEvents)
    {
        stateMachine.Transition(item, next);
        followUps.Add(new FollowUp
        {
            ActionItemId = item.Id,
            SentAt = createdAt,
            Channel = "workflow",
            Response = eventType
        });
        auditEvents.Add(CreateAudit(item, eventType, createdAt));
    }

    /// <summary>Creates an audit event for a follow-up action.</summary>
    private static AuditEvent CreateAudit(ActionItem item, string eventType, DateTime createdAt) =>
        new()
        {
            ActionItemId = item.Id,
            EventType = eventType,
            Actor = "FollowUpService",
            CreatedAt = createdAt
        };

    /// <summary>Persists buffered follow-up and audit records when the current pass produced any.</summary>
    private async Task PersistRecordsAsync(
        IReadOnlyCollection<FollowUp> followUps,
        IReadOnlyCollection<AuditEvent> auditEvents,
        CancellationToken cancellationToken)
    {
        if (followUps.Count > 0)
        {
            await followUpRepository.AddManyAsync(followUps, cancellationToken);
        }

        if (auditEvents.Count > 0)
        {
            await auditEventRepository.AddManyAsync(auditEvents, cancellationToken);
        }
    }
}
