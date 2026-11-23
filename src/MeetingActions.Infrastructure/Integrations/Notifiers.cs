using MeetingActions.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingActions.Infrastructure.Integrations;

/// <summary>Sends notifications to the application log for local development.</summary>
public sealed class ConsoleNotifier(ILogger<ConsoleNotifier> logger) : INotifier
{
    /// <summary>Logs a notification and any available quick-reply choices.</summary>
    public Task SendAsync(string recipient, string message, IReadOnlyList<string>? quickReplyOptions = null, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Notification to {Recipient}: {Message}; Replies: {Replies}",
            recipient, message, quickReplyOptions is null ? "none" : string.Join(", ", quickReplyOptions));
        return Task.CompletedTask;
    }
}

/// <summary>Reserved adapter for sending notifications through a Teams webhook.</summary>
public sealed class TeamsWebhookOptions
{
    /// <summary>Gets or sets the Teams webhook URL placeholder.</summary>
    public string WebhookUrl { get; set; } = string.Empty;
}

/// <summary>Reserved adapter for sending notifications through a Teams webhook.</summary>
public sealed class TeamsWebhookNotifier(IOptions<TeamsWebhookOptions> options, ILogger<TeamsWebhookNotifier> logger) : INotifier
{
    /// <summary>Logs the unimplemented webhook request.</summary>
    public Task SendAsync(string recipient, string message, IReadOnlyList<string>? quickReplyOptions = null, CancellationToken cancellationToken = default)
    {
        _ = options.Value;
        logger.LogInformation("Teams webhook stub requested for {Recipient}: {Message}", recipient, message);
        // TODO: Post an adaptive card to the configured Teams webhook URL.
        return Task.CompletedTask;
    }
}
