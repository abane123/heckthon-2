using MeetingActions.Core.Domain;
using MeetingActions.Core.Services;

namespace MeetingActions.Core.Abstractions;

/// <summary>Creates and queries work items in an external task system.</summary>
public interface ITaskProvider
{
    /// <summary>Creates a ticket for an approved action item.</summary>
    Task<string> CreateTaskAsync(ActionItem item, CancellationToken cancellationToken = default);
    /// <summary>Finds tickets that may duplicate the supplied action.</summary>
    Task<IReadOnlyList<string>> FindSimilarAsync(ActionItem item, CancellationToken cancellationToken = default);
    /// <summary>Gets the current external ticket status.</summary>
    Task<string?> GetStatusAsync(string ticketId, CancellationToken cancellationToken = default);
    /// <summary>Updates the due date of an external ticket.</summary>
    Task UpdateDueDateAsync(string ticketId, DateTime? dueDate, CancellationToken cancellationToken = default);
}

/// <summary>Extracts candidate action items from meeting transcripts.</summary>
public interface ILlmExtractor
{
    /// <summary>Extracts action candidates with their source evidence.</summary>
    Task<AnalysisResult> ExtractAsync(
        string transcript,
        DateTime meetingDate,
        IReadOnlyList<string> attendees,
        CancellationToken cancellationToken = default,
        string meetingTitle = "");
}

/// <summary>Reports whether the selected transcript-analysis provider and model are available.</summary>
public interface ILlmHealth
{
    /// <summary>Checks provider reachability and configured-model availability.</summary>
    Task<LlmHealthResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>Describes the reachability and installation state of an analysis model.</summary>
public sealed record LlmHealthResult(
    bool Reachable,
    bool ModelInstalled,
    string ModelName,
    string? Message = null);

/// <summary>Sends notifications and optional quick-reply choices.</summary>
public interface INotifier
{
    /// <summary>Sends a message to the named recipient.</summary>
    Task SendAsync(string recipient, string message, IReadOnlyList<string>? quickReplyOptions = null, CancellationToken cancellationToken = default);
}

/// <summary>Provides the current time to application services.</summary>
public interface IClock
{
    /// <summary>Gets the current UTC time.</summary>
    DateTime UtcNow { get; }
}

/// <summary>Provides a controllable clock for deterministic demos and tests.</summary>
public sealed class VirtualClock(DateTime? initialTime = null) : IClock
{
    /// <summary>Gets the current virtual UTC time.</summary>
    public DateTime UtcNow { get; private set; } = DateTime.SpecifyKind(initialTime ?? DateTime.UtcNow, DateTimeKind.Utc);

    /// <summary>Moves virtual time forward by the specified number of days.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The requested number of days is negative.</exception>
    public void AdvanceDays(int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);
        UtcNow = UtcNow.AddDays(days);
    }

    /// <summary>Resets the clock to the supplied UTC instant.</summary>
    public void ResetTo(DateTime utcTime)
    {
        UtcNow = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
    }
}

/// <summary>Provides real UTC time.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>Gets the current UTC time.</summary>
    public DateTime UtcNow => DateTime.UtcNow;
}
