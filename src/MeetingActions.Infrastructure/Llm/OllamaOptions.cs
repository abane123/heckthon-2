namespace MeetingActions.Infrastructure.Llm;

/// <summary>Holds the local Ollama endpoint, model, and request-generation settings.</summary>
public sealed class OllamaOptions
{
    /// <summary>Names the configuration section used for Ollama settings.</summary>
    public const string SectionName = "Ollama";

    /// <summary>Names the shared HTTP client used for Ollama requests.</summary>
    public const string HttpClientName = "Ollama";

    /// <summary>Gets or sets the Ollama server URL.</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Gets or sets the model tag used for transcript analysis.</summary>
    public string Model { get; set; } = "llama3.1:8b";

    /// <summary>Gets or sets the explicit token context window sent with each request.</summary>
    public int NumCtx { get; set; } = 16384;

    /// <summary>Gets or sets the model temperature.</summary>
    public double Temperature { get; set; }

    /// <summary>Gets or sets the maximum duration of one Ollama request in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 180;

    /// <summary>Gets or sets the target maximum transcript size for each chunk.</summary>
    public int MaxTranscriptCharsPerChunk { get; set; } = 6000;

    /// <summary>Gets or sets Ollama's keep-alive duration for the selected model.</summary>
    public string KeepAlive { get; set; } = "10m";
}
