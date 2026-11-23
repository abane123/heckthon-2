using System.Diagnostics;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingActions.Infrastructure.Llm;

/// <summary>Uses the built-in extractor only when configured Ollama extraction is unavailable.</summary>
public sealed class FallbackLlmExtractor(
    ILlmExtractor ollamaExtractor,
    ILlmExtractor stubExtractor,
    IOptions<LlmOptions> options,
    ILogger<FallbackLlmExtractor> logger) : ILlmExtractor
{
    /// <summary>Extracts with Ollama and optionally retries through Stub on a typed availability failure.</summary>
    public async Task<AnalysisResult> ExtractAsync(
        string transcript,
        DateTime meetingDate,
        IReadOnlyList<string> attendees,
        CancellationToken cancellationToken = default,
        string meetingTitle = "")
    {
        try
        {
            return await ollamaExtractor.ExtractAsync(
                transcript, meetingDate, attendees, cancellationToken, meetingTitle);
        }
        catch (LlmUnavailableException exception) when (options.Value.FallbackToStub)
        {
            logger.LogWarning(exception, "Ollama is unavailable; using the configured Stub fallback.");
            var stopwatch = Stopwatch.StartNew();
            var fallback = await stubExtractor.ExtractAsync(
                transcript, meetingDate, attendees, cancellationToken, meetingTitle);
            stopwatch.Stop();
            return new AnalysisResult(
                fallback.Items,
                fallback.Decisions,
                fallback.DroppedCount,
                "Stub (fallback)",
                fallback.Model,
                stopwatch.ElapsedMilliseconds,
                fallback.ChunkCount);
        }
    }
}
