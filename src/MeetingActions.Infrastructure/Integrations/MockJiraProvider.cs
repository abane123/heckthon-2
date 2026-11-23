using System.Collections.Concurrent;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;

namespace MeetingActions.Infrastructure.Integrations;

/// <summary>Provides an in-memory Jira-like task provider for local demos.</summary>
public sealed class MockJiraProvider : ITaskProvider
{
    private readonly ConcurrentDictionary<string, string> tickets = new();
    private int sequence;

    /// <summary>Creates a mock ticket and stores its initial status.</summary>
    public Task<string> CreateTaskAsync(ActionItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ticketId = $"MOCK-{Interlocked.Increment(ref sequence):D4}";
        tickets[ticketId] = "Open";
        return Task.FromResult(ticketId);
    }

    /// <summary>Returns no similar tickets for this basic mock.</summary>
    public Task<IReadOnlyList<string>> FindSimilarAsync(ActionItem item, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Returns the stored ticket status when known.</summary>
    public Task<string?> GetStatusAsync(string ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(tickets.TryGetValue(ticketId, out var status) ? status : null);

    /// <summary>Accepts a due-date update without external side effects.</summary>
    public Task UpdateDueDateAsync(string ticketId, DateTime? dueDate, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
