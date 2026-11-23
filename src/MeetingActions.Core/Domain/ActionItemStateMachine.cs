namespace MeetingActions.Core.Domain;

/// <summary>Validates and applies action-item workflow transitions.</summary>
public sealed class ActionItemStateMachine
{
    private static readonly IReadOnlyDictionary<ActionStatus, ActionStatus[]> Transitions =
        new Dictionary<ActionStatus, ActionStatus[]>
        {
            [ActionStatus.Draft] = [ActionStatus.Approved, ActionStatus.Rejected],
            // Approval can be undone during organizer review before ticket creation.
            [ActionStatus.Approved] = [ActionStatus.Created, ActionStatus.Done, ActionStatus.Draft],
            [ActionStatus.Created] = [ActionStatus.InProgress, ActionStatus.Done],
            [ActionStatus.InProgress] = [ActionStatus.Overdue, ActionStatus.Done],
            [ActionStatus.Overdue] = [ActionStatus.InProgress, ActionStatus.Escalated, ActionStatus.Done],
            [ActionStatus.Escalated] = [ActionStatus.Done],
            [ActionStatus.Done] = [],
            // Rejected items can be restored to review by an explicit organizer action.
            [ActionStatus.Rejected] = [ActionStatus.Draft]
        };

    /// <summary>Returns whether the requested transition is permitted.</summary>
    public bool CanTransition(ActionStatus from, ActionStatus to) =>
        Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>Transitions the item or throws when the transition is invalid.</summary>
    /// <exception cref="InvalidOperationException">The transition is not permitted.</exception>
    public void Transition(ActionItem item, ActionStatus to)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!CanTransition(item.Status, to))
        {
            throw new InvalidOperationException($"Cannot transition an action from {item.Status} to {to}.");
        }

        item.Status = to;
    }
}
