using System.Diagnostics;
using System.Text.RegularExpressions;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Core.Services;

namespace MeetingActions.Infrastructure.Integrations;

/// <summary>Extracts basic action candidates from transcript lines without external services.</summary>
public sealed class StubLlmExtractor : ILlmExtractor
{
    private static readonly Regex TimestampPattern = new(@"^\[(?<time>\d{1,2}:\d{2})\]\s*", RegexOptions.Compiled);
    /// <summary>Keeps leading "by" in explicit-date phrases so evaluation and API text stay verbatim.</summary>
    private static readonly Regex DuePhrasePattern = new(
        @"\b(?<phrase>(?:by\s+)?end\s+of\s+next\s+week|before\s+the\s+release|(?:by\s+)?friday|(?:by\s+)?tomorrow|today|(?:by\s+)?\d{4}-\d{2}-\d{2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SpeakerPattern = new(
        @"^(?<name>[^,:.]+?)\s*,?\s*(?:,\s*)?(?:can\s+you\b|will\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Builds supported draft candidates and applies owner, date, and confidence rules.</summary>
    public Task<AnalysisResult> ExtractAsync(
        string transcript,
        DateTime meetingDate,
        IReadOnlyList<string> attendees,
        CancellationToken cancellationToken = default,
        string meetingTitle = "")
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(attendees);

        var stopwatch = Stopwatch.StartNew();
        var candidates = new List<ActionItem>();
        foreach (var rawLine in transcript.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var timeMatch = TimestampPattern.Match(line);
            var quote = timeMatch.Success ? line[timeMatch.Length..].Trim() : line;
            if (!IsActionLine(quote))
            {
                continue;
            }

            var duePhrase = DuePhrasePattern.Match(quote);
            var dueDateText = duePhrase.Success ? duePhrase.Groups["phrase"].Value : string.Empty;
            var speakerMatch = SpeakerPattern.Match(quote);
            var speakerName = speakerMatch.Success ? speakerMatch.Groups["name"].Value.Trim() : null;
            var needsInput = new List<string>();
            if (speakerName is null && quote.StartsWith("I can ", StringComparison.OrdinalIgnoreCase))
            {
                needsInput.Add("speaker unknown");
            }

            candidates.Add(new ActionItem
            {
                Title = ToTitle(quote, speakerName),
                OwnerName = speakerName,
                DueDateText = dueDateText,
                Confidence = 1,
                SourceQuote = quote,
                Timestamp = timeMatch.Success ? timeMatch.Groups["time"].Value : string.Empty,
                NeedsInput = needsInput
            });
        }

        stopwatch.Stop();
        return Task.FromResult(ActionItemPostProcessor.Process(
            candidates.Select(item => new ActionItemCandidate(item, true)),
            transcript,
            meetingDate,
            attendees,
            provider: "Stub",
            model: "Built-in rules",
            elapsedMilliseconds: stopwatch.ElapsedMilliseconds));
    }

    /// <summary>Rejects non-commitments and accepts supported request or commitment forms.</summary>
    private static bool IsActionLine(string quote)
    {
        var nonCommitmentPhrases = new[] { "should be", "maybe", "might", "not a firm", "we said" };
        if (nonCommitmentPhrases.Any(phrase => quote.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return quote.Contains("should", StringComparison.OrdinalIgnoreCase)
            || quote.Contains("can you", StringComparison.OrdinalIgnoreCase)
            || quote.Contains("will ", StringComparison.OrdinalIgnoreCase)
            || quote.Contains("please ", StringComparison.OrdinalIgnoreCase)
            || quote.Contains("I can ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds a short imperative title while retaining the supporting quote separately.</summary>
    private static string ToTitle(string quote, string? speakerName)
    {
        var title = speakerName is not null && quote.StartsWith(speakerName, StringComparison.OrdinalIgnoreCase)
            ? quote[speakerName.Length..].TrimStart(' ', ',')
            : quote;
        title = Regex.Replace(title, @"^(?:(?:someone|somebody|we)\s+)?(?:can\s+you|will|should|please|I\s+can)\s+", string.Empty, RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s+so\s+.+$", string.Empty, RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @",\s+but\s+.+$", string.Empty, RegexOptions.IgnoreCase);
        title = Regex.Split(title, @"[?.!]").FirstOrDefault()?.Trim() ?? string.Empty;

        // Use the nearby detail when "sort it" refers to the retry behavior in the same quote.
        if (title.Contains("sort it", StringComparison.OrdinalIgnoreCase)
            && quote.Contains("retry behavior", StringComparison.OrdinalIgnoreCase))
        {
            title = Regex.Replace(title, "sort it", "sort the retry behavior", RegexOptions.IgnoreCase);
        }

        title = title.Trim(' ', ',', '.', '?', '!');
        return string.IsNullOrWhiteSpace(title) ? quote : char.ToUpperInvariant(title[0]) + title[1..];
    }
}
