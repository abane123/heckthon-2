namespace MeetingActions.Infrastructure.Llm;

/// <summary>Controls the active transcript-analysis provider and optional fallback behavior.</summary>
public sealed class LlmOptions
{
    /// <summary>Names the configuration section used for LLM provider selection.</summary>
    public const string SectionName = "Llm";

    /// <summary>Gets or sets the active provider name; Stub is the safe default.</summary>
    public string Provider { get; set; } = "Stub";

    /// <summary>Gets or sets whether extraction may fall back to Stub when Ollama is unavailable.</summary>
    public bool FallbackToStub { get; set; }
}
