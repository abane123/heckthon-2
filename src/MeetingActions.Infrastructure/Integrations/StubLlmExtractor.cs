using System.Globalization;
using System.Text.RegularExpressions;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;

namespace MeetingActions.Infrastructure.Integrations;

/// <summary>Extracts basic action candidates from transcript lines without external services.</summary>
public sealed class StubLlmExtractor : ILlmExtractor
{
    private static readonly Regex TimestampPattern = new(@"^\[(?<time>\d{1,2}:\d{2})\]\s*", RegexOptions.Compiled);
    private static readonly Regex ExplicitDatePattern = new(@"\b(?<date>\d{4}-\d{2}-\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex DuePhrasePattern = new(
        @"\b(?<phrase>by\s+end\s+of\s+next\s+week|before\s+the\s+release|by\s+friday|tomorrow|\d{4}-\d{2}-\d{2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SpeakerPattern = new(
        @"^(?<name>[^,:.]+?)\s*,?\s*(?:,\s*)?(?:can\s+you\b|will\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Builds supported draft candidates and applies owner, date, and confidence rules.</summary>
    public Task<IReadOnlyList<ActionItem>> ExtractAsync(string transcript, DateTime meetingDate, IReadOnlyList<string> attendees, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(attendees);

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
            var dueDate = ResolveDueDate(dueDateText, meetingDate);
            var speakerMatch = SpeakerPattern.Match(quote);
            var speakerName = speakerMatch.Success ? speakerMatch.Groups["name"].Value.Trim() : null;
            var owner = ResolveAttendee(speakerName, attendees);
            var needsInput = new List<string>();
            if (owner is null)
            {
                needsInput.Add("owner");
            }

            if ((speakerName is not null && owner is null && !IsVagueSpeaker(speakerName))
                || (speakerName is null && quote.StartsWith("I can ", StringComparison.OrdinalIgnoreCase)))
            {
                needsInput.Add("speaker unknown");
            }

            if (dueDate is null)
            {
                needsInput.Add("due date");
            }

            var confidence = 0.5
                + (owner is null ? 0 : 0.25)
                + (dueDate is null ? 0 : 0.2)
                - (HasTentativeLanguage(quote) ? 0.2 : 0);

            candidates.Add(new ActionItem
            {
                Title = ToTitle(quote, speakerName),
                OwnerName = owner,
                DueDate = dueDate,
                DueDateText = dueDateText,
                Confidence = Math.Min(0.95, confidence),
                SourceQuote = quote,
                Timestamp = timeMatch.Success ? timeMatch.Groups["time"].Value : string.Empty,
                NeedsInput = needsInput
            });
        }

        return Task.FromResult<IReadOnlyList<ActionItem>>(ActionItemExtractionRules.Apply(candidates));
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

    /// <summary>Returns the matching attendee's canonical name, excluding vague speaker labels.</summary>
    private static string? ResolveAttendee(string? speakerName, IReadOnlyList<string> attendees)
    {
        if (string.IsNullOrWhiteSpace(speakerName) || IsVagueSpeaker(speakerName))
        {
            return null;
        }

        return attendees.FirstOrDefault(attendee => attendee.Equals(speakerName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves supported due-date phrases relative to the meeting date.</summary>
    private static DateTime? ResolveDueDate(string dueDateText, DateTime meetingDate)
    {
        var day = meetingDate.Date;
        if (dueDateText.Equals("tomorrow", StringComparison.OrdinalIgnoreCase))
        {
            return day.AddDays(1);
        }

        if (dueDateText.Equals("by Friday", StringComparison.OrdinalIgnoreCase))
        {
            var daysUntilFriday = ((int)DayOfWeek.Friday - (int)day.DayOfWeek + 7) % 7;
            return day.AddDays(daysUntilFriday == 0 ? 7 : daysUntilFriday);
        }

        if (dueDateText.Equals("by end of next week", StringComparison.OrdinalIgnoreCase))
        {
            var daysSinceMonday = ((int)day.DayOfWeek + 6) % 7;
            return day.AddDays(-daysSinceMonday + 11);
        }

        if (ExplicitDatePattern.Match(dueDateText) is { Success: true } explicitDate
            && DateTime.TryParseExact(explicitDate.Groups["date"].Value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            return parsedDate;
        }

        return null;
    }

    /// <summary>Identifies tentative wording for the confidence penalty.</summary>
    private static bool HasTentativeLanguage(string quote) =>
        quote.Contains("should", StringComparison.OrdinalIgnoreCase)
        || quote.Contains("could", StringComparison.OrdinalIgnoreCase)
        || quote.Contains("I can ", StringComparison.OrdinalIgnoreCase)
        || quote.Contains("someone", StringComparison.OrdinalIgnoreCase)
        || quote.Contains("somebody", StringComparison.OrdinalIgnoreCase);

    /// <summary>Identifies generic speakers that must not be assigned as owners.</summary>
    private static bool IsVagueSpeaker(string speakerName) =>
        speakerName.Equals("someone", StringComparison.OrdinalIgnoreCase)
        || speakerName.Equals("somebody", StringComparison.OrdinalIgnoreCase)
        || speakerName.Equals("we", StringComparison.OrdinalIgnoreCase);

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
