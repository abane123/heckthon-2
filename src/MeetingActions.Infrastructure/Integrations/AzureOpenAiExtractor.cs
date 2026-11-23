using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using Microsoft.Extensions.Options;

namespace MeetingActions.Infrastructure.Integrations;

/// <summary>Defines Azure OpenAI connection settings without storing credentials in source.</summary>
public sealed class AzureOpenAiOptions
{
    /// <summary>Gets or sets the Azure OpenAI endpoint placeholder.</summary>
    public string Endpoint { get; set; } = string.Empty;
    /// <summary>Gets or sets the deployment name placeholder.</summary>
    public string Deployment { get; set; } = string.Empty;
    /// <summary>Gets or sets the API key placeholder, supplied through configuration.</summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>Reserved adapter for a future Azure OpenAI-backed extractor.</summary>
public sealed class AzureOpenAiExtractor(IOptions<AzureOpenAiOptions> options) : ILlmExtractor
{
    /// <summary>Indicates that external extraction has not been configured or implemented.</summary>
    public Task<IReadOnlyList<ActionItem>> ExtractAsync(string transcript, DateTime meetingDate, IReadOnlyList<string> attendees, CancellationToken cancellationToken = default)
    {
        _ = options.Value;
        // TODO: Add Azure OpenAI request/response mapping using the configured endpoint and deployment.
        throw new NotSupportedException("Azure OpenAI extraction is a stub; configure a real adapter before selecting it.");
    }
}
