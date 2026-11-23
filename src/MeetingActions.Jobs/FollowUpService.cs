using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeetingActions.Jobs;

/// <summary>Runs deadline reminders, overdue checks, and escalations for open action items.</summary>
public sealed class FollowUpService(
    MeetingActionsDbContext dbContext,
    ITaskProvider taskProvider,
    INotifier notifier,
    IClock clock,
    ActionItemStateMachine stateMachine,
    ILogger<FollowUpService> logger)
{
    private static readonly string[] QuickReplies = ["Done", "PushToFriday", "Blocked", "Reassign"];

    /// <summary>Processes action items once and returns the reminders sent during this pass.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        // Count only reminder notifications so callers can report useful simulation feedback.
        var remindersSent = 0;
        var items = await dbContext.ActionItems.Include(item => item.Meeting).ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            if (item.Status is ActionStatus.Done or ActionStatus.Rejected or ActionStatus.Draft or ActionStatus.Approved
                || item.DueDate is null || string.IsNullOrWhiteSpace(item.ExternalTicketId))
            {
                continue;
            }

            var recipient = item.OwnerName ?? "Organizer";
            if (item.DueDate.Value.Date == now.Date.AddDays(1) && item.Status is ActionStatus.Created or ActionStatus.InProgress)
            {
                await RecordNotificationAsync(item, recipient, "Reminder", "This action is due tomorrow.", "reminder", now, null, cancellationToken);
                remindersSent++;
            }

            if (item.DueDate.Value.Date > now.Date)
            {
                continue;
            }

            var externalStatus = await taskProvider.GetStatusAsync(item.ExternalTicketId, cancellationToken);
            dbContext.FollowUps.Add(new FollowUp
            {
                ActionItemId = item.Id,
                SentAt = now,
                Channel = "ticket-status-check",
                Response = externalStatus ?? "Unknown"
            });
            dbContext.AuditEvents.Add(new AuditEvent
            {
                ActionItemId = item.Id,
                EventType = $"Ticket status checked: {externalStatus ?? "Unknown"}",
                Actor = "FollowUpService",
                CreatedAt = now
            });
            if (string.Equals(externalStatus, "Done", StringComparison.OrdinalIgnoreCase)
                || string.Equals(externalStatus, "Closed", StringComparison.OrdinalIgnoreCase))
            {
                TransitionAndAudit(item, ActionStatus.Done, "External ticket completed", now);
                continue;
            }

            if (item.Status is ActionStatus.Created or ActionStatus.InProgress)
            {
                if (item.Status == ActionStatus.Created)
                {
                    TransitionAndAudit(item, ActionStatus.InProgress, "Work started for deadline tracking", now);
                }

                TransitionAndAudit(item, ActionStatus.Overdue, "Action became overdue", now);
            }

            if (item.Status is ActionStatus.Overdue or ActionStatus.Escalated)
            {
                var dailyOverdue = await dbContext.FollowUps.AnyAsync(
                    followUp => followUp.ActionItemId == item.Id && followUp.Channel == "overdue" && followUp.SentAt.Date == now.Date,
                    cancellationToken);
                if (dailyOverdue)
                {
                    continue;
                }

                item.SlipCount++;
                await RecordNotificationAsync(item, recipient, "Overdue", $"Action slipped {item.SlipCount} time(s).", "overdue", now, QuickReplies, cancellationToken);
                if (item.SlipCount >= 2 && item.Status == ActionStatus.Overdue)
                {
                    TransitionAndAudit(item, ActionStatus.Escalated, "Escalated after two slips", now);
                    await RecordNotificationAsync(item, recipient, "Escalation", "This action has been escalated after repeated slips.", "escalation", now, QuickReplies, cancellationToken);
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Follow-up run completed at {Time}", now);
        return remindersSent;
    }

    private async Task RecordNotificationAsync(ActionItem item, string recipient, string label, string message, string channel,
        DateTime sentAt, IReadOnlyList<string>? quickReplies, CancellationToken cancellationToken)
    {
        await notifier.SendAsync(recipient, message, quickReplies, cancellationToken);
        dbContext.FollowUps.Add(new FollowUp { ActionItemId = item.Id, SentAt = sentAt, Channel = channel });
        dbContext.AuditEvents.Add(new AuditEvent
        {
            ActionItemId = item.Id,
            EventType = label,
            Actor = "FollowUpService",
            CreatedAt = sentAt
        });
    }

    private void TransitionAndAudit(ActionItem item, ActionStatus next, string eventType, DateTime createdAt)
    {
        stateMachine.Transition(item, next);
        dbContext.FollowUps.Add(new FollowUp
        {
            ActionItemId = item.Id,
            SentAt = createdAt,
            Channel = "workflow",
            Response = eventType
        });
        dbContext.AuditEvents.Add(new AuditEvent
        {
            ActionItemId = item.Id,
            EventType = eventType,
            Actor = "FollowUpService",
            CreatedAt = createdAt
        });
    }
}
