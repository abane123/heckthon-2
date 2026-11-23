namespace MeetingActions.Infrastructure.Llm;

/// <summary>Describes an actionable failure to reach the configured transcript-analysis provider.</summary>
public sealed class LlmUnavailableException : Exception
{
    /// <summary>Creates a provider-unavailable error with a safe, actionable message.</summary>
    public LlmUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
