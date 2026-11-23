namespace MeetingActions.Core.Domain;

/// <summary>Applies evidence and organizer-input rules to extracted candidates.</summary>
public static class ActionItemExtractionRules
{
    /// <summary>Drops unsupported candidates and records missing required details.</summary>
    public static IReadOnlyList<ActionItem> Apply(IEnumerable<ActionItem> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.SourceQuote))
            .Select(item =>
            {
                if (item.OwnerId is null && string.IsNullOrWhiteSpace(item.OwnerName)
                    && !item.NeedsInput.Contains("owner", StringComparer.OrdinalIgnoreCase)
                    && !item.NeedsInput.Contains("speaker unknown", StringComparer.OrdinalIgnoreCase))
                {
                    item.NeedsInput.Add("owner");
                }

                if (item.DueDate is null && !item.NeedsInput.Contains("due date", StringComparer.OrdinalIgnoreCase))
                {
                    item.NeedsInput.Add("due date");
                }

                return item;
            })
            .ToArray();
    }
}
