using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingActions.Infrastructure.Integrations;

/// <summary>Defines Azure DevOps connection settings supplied through configuration.</summary>
public sealed class AzureDevOpsOptions
{
    /// <summary>Gets or sets the organization URL placeholder.</summary>
    public string OrganizationUrl { get; set; } = string.Empty;
    /// <summary>Gets or sets the project name placeholder.</summary>
    public string Project { get; set; } = string.Empty;
    /// <summary>Gets or sets the personal access token supplied through user-secrets.</summary>
    public string PersonalAccessToken { get; set; } = string.Empty;
}

/// <summary>Stub task provider that generates predictable Azure DevOps ticket identifiers.</summary>
public sealed class AzureDevOpsProvider(IOptions<AzureDevOpsOptions> options, ILogger<AzureDevOpsProvider> logger) : ITaskProvider
{
    /// <summary>Logs the stub operation and returns a generated ticket identifier.</summary>
    public Task<string> CreateTaskAsync(ActionItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = options.Value;
        var ticketId = $"ADO-{item.Id.ToString("N")[..8].ToUpperInvariant()}";
        logger.LogInformation("Stub Azure DevOps task created: {TicketId} for {Title}", ticketId, item.Title);
        // TODO: Replace this stub with the Azure DevOps Work Items REST API call.
        return Task.FromResult(ticketId);
    }

    /// <summary>Returns no duplicate candidates in the stub implementation.</summary>
    public Task<IReadOnlyList<string>> FindSimilarAsync(ActionItem item, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Returns an active status for stub-created tickets.</summary>
    public Task<string?> GetStatusAsync(string ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>("Active");

    /// <summary>Logs a due-date update requested for a stub ticket.</summary>
    public Task UpdateDueDateAsync(string ticketId, DateTime? dueDate, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Stub Azure DevOps due date update for {TicketId}: {DueDate}", ticketId, dueDate);
        // TODO: Update the due-date field through Azure DevOps REST API.
        return Task.CompletedTask;
    }
}
