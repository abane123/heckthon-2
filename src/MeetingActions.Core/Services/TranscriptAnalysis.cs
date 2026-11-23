using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MeetingActions.Core.Domain;

namespace MeetingActions.Core.Services;

/// <summary>Pairs an extracted action with the model's commitment classification.</summary>
public sealed record ActionItemCandidate
{
    /// <summary>Creates a proposed action and its commitment classification.</summary>
    public ActionItemCandidate(ActionItem item, bool isCommitment)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        IsCommitment = isCommitment;
    }

    /// <summary>Gets the proposed action item.</summary>
    public ActionItem Item { get; }

    /// <summary>Gets whether the proposal represents a commitment.</summary>
    public bool IsCommitment { get; }
}

/// <summary>Contains the deterministic result and metadata for one transcript analysis.</summary>
public sealed record AnalysisResult
{
    /// <summary>Creates a completed analysis result.</summary>
    public AnalysisResult(
        IReadOnlyList<ActionItem> items,
        IReadOnlyList<string> decisions,
        int droppedCount,
        string provider,
        string model,
        long elapsedMilliseconds,
        int chunkCount,
        IReadOnlyList<string>? warnings = null)
    {
        Items = items;
        Decisions = decisions;
        DroppedCount = droppedCount;
        Provider = provider;
        Model = model;
        ElapsedMilliseconds = elapsedMilliseconds;
        ChunkCount = chunkCount;
        Warnings = warnings ?? [];
    }

    /// <summary>Gets the accepted action items.</summary>
    public IReadOnlyList<ActionItem> Items { get; }

    /// <summary>Gets decisions separated from action items.</summary>
    public IReadOnlyList<string> Decisions { get; }

    /// <summary>Gets the number of proposals dropped because their source quote was not verified.</summary>
    public int DroppedCount { get; }

    /// <summary>Gets the provider that produced the proposals.</summary>
    public string Provider { get; }

    /// <summary>Gets the configured model or rules-engine name.</summary>
    public string Model { get; }

    /// <summary>Gets the elapsed extraction and post-processing time in milliseconds.</summary>
    public long ElapsedMilliseconds { get; }

    /// <summary>Gets the number of transcript chunks sent to the provider.</summary>
    public int ChunkCount { get; }

    /// <summary>Gets non-fatal analysis warnings, such as a prompt approaching the model context limit.</summary>
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>Stores optional provider analysis details returned on a meeting response.</summary>
public sealed record MeetingAnalysisMetadata
{
    /// <summary>Creates the API-facing metadata for a completed meeting analysis.</summary>
    public MeetingAnalysisMetadata(
        string provider,
        string model,
        long durationMs,
        int droppedItems,
        int chunkCount,
        IReadOnlyList<string>? warnings = null)
    {
        Provider = provider;
        Model = model;
        DurationMs = durationMs;
        DroppedItems = droppedItems;
        ChunkCount = chunkCount;
        Warnings = warnings ?? [];
    }

    /// <summary>Gets the provider label for the analysis run.</summary>
    public string Provider { get; }

    /// <summary>Gets the model name or built-in engine label.</summary>
    public string Model { get; }

    /// <summary>Gets the analysis duration in milliseconds.</summary>
    public long DurationMs { get; }

    /// <summary>Gets the count of hallucinated-quote proposals dropped.</summary>
    public int DroppedItems { get; }

    /// <summary>Gets the number of transcript chunks analyzed.</summary>
    public int ChunkCount { get; }

    /// <summary>Gets non-fatal warnings for this analysis.</summary>
    public IReadOnlyList<string> Warnings { get; }
}

/// <summary>Resolves supported transcript due-date phrases relative to the meeting date.</summary>
public static class DueDateResolver
{
    /// <summary>Finds explicit ISO calendar dates inside the original phrase.</summary>
    private static readonly Regex ExplicitDatePattern = new(
        @"\b(?<date>\d{4}-\d{2}-\d{2})\b",
        RegexOptions.Compiled);
    /// <summary>Recognizes day-of-month deadlines while preserving the original phrase in the action item.</summary>
    private static readonly Regex OrdinalDayPattern = new(
        @"^(?:by(?:\s+the)?\s+)?(?<day>\d{1,2})(?:st|nd|rd|th)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>Tokenizes phrases without dropping Devanagari combining marks.</summary>
    private static readonly Regex PhraseTokenPattern = new(@"[\p{L}\p{M}\p{N}]+", RegexOptions.Compiled);
    /// <summary>Maps common English, Roman Hindi, and Devanagari weekday spellings.</summary>
    private static readonly IReadOnlyDictionary<string, DayOfWeek> Weekdays =
        new Dictionary<string, DayOfWeek>(StringComparer.Ordinal)
        {
            ["monday"] = DayOfWeek.Monday,
            ["tuesday"] = DayOfWeek.Tuesday,
            ["wednesday"] = DayOfWeek.Wednesday,
            ["thursday"] = DayOfWeek.Thursday,
            ["friday"] = DayOfWeek.Friday,
            ["saturday"] = DayOfWeek.Saturday,
            ["sunday"] = DayOfWeek.Sunday,
            ["somvar"] = DayOfWeek.Monday,
            ["somvaar"] = DayOfWeek.Monday,
            ["सोमवार"] = DayOfWeek.Monday,
            ["mangalvar"] = DayOfWeek.Tuesday,
            ["mangalvaar"] = DayOfWeek.Tuesday,
            ["मंगलवार"] = DayOfWeek.Tuesday,
            ["budhvar"] = DayOfWeek.Wednesday,
            ["budhwar"] = DayOfWeek.Wednesday,
            ["बुधवार"] = DayOfWeek.Wednesday,
            ["guruvar"] = DayOfWeek.Thursday,
            ["guruwar"] = DayOfWeek.Thursday,
            ["गुरुवार"] = DayOfWeek.Thursday,
            ["shukravar"] = DayOfWeek.Friday,
            ["shukrawar"] = DayOfWeek.Friday,
            ["शुक्रवार"] = DayOfWeek.Friday,
            ["shanivar"] = DayOfWeek.Saturday,
            ["shanivaar"] = DayOfWeek.Saturday,
            ["शनिवार"] = DayOfWeek.Saturday,
            ["ravivar"] = DayOfWeek.Sunday,
            ["ravivaar"] = DayOfWeek.Sunday,
            ["रविवार"] = DayOfWeek.Sunday
        };

    /// <summary>Resolves supported English, Hindi, and Hinglish date phrases relative to the meeting date.</summary>
    public static DateTime? Resolve(string? dueDateText, DateTime meetingDate)
    {
        if (string.IsNullOrWhiteSpace(dueDateText))
        {
            return null;
        }

        var phrase = NormalizePhrase(dueDateText);
        var day = meetingDate.Date;
        if (phrase is "today" or "by today" or "aaj" or "आज" or "eod" or "by eod" or "cob" or "by cob")
        {
            return day;
        }

        if (phrase is "tomorrow" or "by tomorrow" or "kal tak" or "कल तक")
        {
            return day.AddDays(1);
        }

        if (phrase is "parson" or "parson tak" or "परसों" or "परसों तक")
        {
            return day.AddDays(2);
        }

        if (phrase is "end of next week" or "by end of next week"
            or "next week" or "by next week"
            or "agle hafte" or "agle hafte tak" or "अगले हफ्ते" or "अगले हफ्ते तक"
            or "अगले सप्ताह" or "अगले सप्ताह तक")
        {
            return FridayOfNextWeek(day);
        }

        if (phrase is "this friday" or "is hafte ke end tak" or "इस हफ्ते के अंत तक"
            or "इस सप्ताह के अंत तक")
        {
            return FridayInCurrentWeek(day);
        }

        if (phrase is "month end" or "end of month" or "by end of month"
            or "mahine ke end" or "mahine ke end tak" or "mahine ke ant tak"
            or "महीने के अंत" or "महीने के अंत तक")
        {
            return new DateTime(day.Year, day.Month, DateTime.DaysInMonth(day.Year, day.Month));
        }

        var ordinalMatch = OrdinalDayPattern.Match(phrase);
        if (ordinalMatch.Success
            && int.TryParse(ordinalMatch.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var dayOfMonth)
            && dayOfMonth is >= 1 and <= 31)
        {
            return NextOccurrenceOfDay(day, dayOfMonth);
        }

        var weekdayPhrase = phrase;
        if (weekdayPhrase.StartsWith("by ", StringComparison.Ordinal))
        {
            weekdayPhrase = weekdayPhrase[3..];
        }

        if (weekdayPhrase.EndsWith(" tak", StringComparison.Ordinal))
        {
            weekdayPhrase = weekdayPhrase[..^4];
        }
        else if (weekdayPhrase.EndsWith(" तक", StringComparison.Ordinal))
        {
            weekdayPhrase = weekdayPhrase[..^3];
        }

        if (Weekdays.TryGetValue(weekdayPhrase, out var weekday))
        {
            return NextWeekday(day, weekday);
        }

        if (ExplicitDatePattern.Match(dueDateText) is { Success: true } explicitDate
            && DateTime.TryParseExact(
                explicitDate.Groups["date"].Value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate))
        {
            return parsedDate;
        }

        return null;
    }

    /// <summary>Normalizes whitespace, punctuation, and Unicode composition while preserving original stored text.</summary>
    private static string NormalizePhrase(string value) =>
        string.Join(' ', PhraseTokenPattern.Matches(value.Normalize(NormalizationForm.FormC))
            .Select(match => match.Value.ToLowerInvariant()));

    /// <summary>Finds the next date on the requested weekday, treating a same-day weekday as next week.</summary>
    private static DateTime NextWeekday(DateTime from, DayOfWeek weekday)
    {
        var daysUntil = ((int)weekday - (int)from.DayOfWeek + 7) % 7;
        return from.AddDays(daysUntil == 0 ? 7 : daysUntil);
    }

    /// <summary>Finds Friday in the current calendar week, including today when the meeting is Friday.</summary>
    private static DateTime FridayInCurrentWeek(DateTime from)
    {
        var daysUntilFriday = ((int)DayOfWeek.Friday - (int)from.DayOfWeek + 7) % 7;
        return from.AddDays(daysUntilFriday);
    }

    /// <summary>Finds Friday of the week following the meeting's calendar week.</summary>
    private static DateTime FridayOfNextWeek(DateTime from)
    {
        var daysSinceMonday = ((int)from.DayOfWeek + 6) % 7;
        return from.AddDays(-daysSinceMonday + 11);
    }

    /// <summary>Finds the next calendar occurrence of an ordinal day that exists in its month.</summary>
    private static DateTime? NextOccurrenceOfDay(DateTime from, int dayOfMonth)
    {
        for (var monthOffset = 0; monthOffset <= 12; monthOffset++)
        {
            var month = new DateTime(from.Year, from.Month, 1).AddMonths(monthOffset);
            if (dayOfMonth <= DateTime.DaysInMonth(month.Year, month.Month))
            {
                var candidate = new DateTime(month.Year, month.Month, dayOfMonth);
                if (candidate >= from)
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}

/// <summary>Validates transcript evidence and resolves organizer-facing action fields deterministically.</summary>
public static class ActionItemPostProcessor
{
    private static readonly Regex WordPattern = new(@"[\p{L}\p{M}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex SpeakerOwnerPattern = new(
        @"^\s*(?<name>[\p{L}][\p{L}\p{M}'-]*(?:\s+[\p{L}][\p{L}\p{M}'-]*)?)\s*[,:\-]?\s+(?:can\s+you|will|should|please|(?:kar|bhej|dekh|likh)\s+(?:dena|dijiye|do)|कर\s+देना|कर\s+दीजिए|भेज\s+देना|भेज\s+दीजिए)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TimestampPrefixPattern = new(
        @"^\s*\[\d{1,2}:\d{2}\]\s*",
        RegexOptions.Compiled);
    private static readonly Regex HedgePattern = new(
        @"\b(?:maybe|perhaps|might|possibly|could\s+be|should\s+be|(?:somebody|someone)\s+should|we\s+said|shayad|ho\s+sakta\s+hai|dekhte\s+hain|baad\s+mein\s+dekhte\s+hain|pata\s+nahi|try\s+karenge|kabhi|kisi\s+ko\s+dekhna\s+chahiye|karna\s+chahiye|karna\s+padega)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FirstPersonCommitmentPattern = new(
        @"^\s*(?:i\s+(?:can|will)|i'll|let\s+me|main|mai|मैं)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>Honorifics and suffixes excluded from attendee identity comparisons.</summary>
    private static readonly HashSet<string> Honorifics = new(StringComparer.Ordinal)
    {
        "mr", "mrs", "ms", "sir", "madam", "maam", "bhai", "bhaiya", "didi", "saab", "sahab", "ji"
    };
    /// <summary>Single-token English, Hindi, and Hinglish speech fillers omitted only during quote comparison.</summary>
    private static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal)
    {
        "um", "uh", "er", "hmm", "like", "basically", "actually", "so", "right", "okay",
        "matlab", "yaar", "na", "haan", "achha", "accha", "toh", "bas", "arre", "dekho", "suno", "chalo"
    };
    // Multiword fillers are removed as sequences so their component words remain valid evidence elsewhere.
    private static readonly string[][] FillerPhrases =
    [
        ["you", "know"],
        ["i", "mean"],
        ["haan", "haan"],
        ["theek", "hai"]
    ];
    /// <summary>Common first-name nicknames used only during owner matching.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> NicknameAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["alex"] = ["alexander", "alexandra"],
            ["alexander"] = ["alex"],
            ["alexandra"] = ["alex"],
            ["mike"] = ["michael"],
            ["michael"] = ["mike"],
            ["bob"] = ["robert"],
            ["rob"] = ["robert"],
            ["robert"] = ["bob", "rob"],
            ["bill"] = ["william"],
            ["will"] = ["william"],
            ["william"] = ["bill", "will"],
            ["liz"] = ["elizabeth"],
            ["beth"] = ["elizabeth"],
            ["elizabeth"] = ["liz", "beth"]
        };
    // The small consonant/vowel maps intentionally cover common Devanagari name spellings without translating quotes.
    private static readonly IReadOnlyDictionary<char, string> DevanagariConsonants =
        new Dictionary<char, string>
        {
            ['क'] = "k", ['ख'] = "kh", ['ग'] = "g", ['घ'] = "gh", ['ङ'] = "n",
            ['च'] = "ch", ['छ'] = "chh", ['ज'] = "j", ['झ'] = "jh", ['ञ'] = "ny",
            ['ट'] = "t", ['ठ'] = "th", ['ड'] = "d", ['ढ'] = "dh", ['ण'] = "n",
            ['त'] = "t", ['थ'] = "th", ['द'] = "d", ['ध'] = "dh", ['न'] = "n",
            ['प'] = "p", ['फ'] = "ph", ['ब'] = "b", ['भ'] = "bh", ['म'] = "m",
            ['य'] = "y", ['र'] = "r", ['ल'] = "l", ['व'] = "v", ['श'] = "sh",
            ['ष'] = "sh", ['स'] = "s", ['ह'] = "h"
        };
    private static readonly IReadOnlyDictionary<char, string> DevanagariVowels =
        new Dictionary<char, string>
        {
            ['अ'] = "a", ['आ'] = "aa", ['इ'] = "i", ['ई'] = "ii", ['उ'] = "u",
            ['ऊ'] = "uu", ['ऋ'] = "ri", ['ए'] = "e", ['ऐ'] = "ai", ['ओ'] = "o", ['औ'] = "au"
        };
    private static readonly IReadOnlyDictionary<char, string> DevanagariVowelSigns =
        new Dictionary<char, string>
        {
            ['ा'] = "aa", ['ि'] = "i", ['ी'] = "ii", ['ु'] = "u", ['ू'] = "uu",
            ['ृ'] = "ri", ['े'] = "e", ['ै'] = "ai", ['ो'] = "o", ['ौ'] = "au"
        };

    /// <summary>Verifies evidence, filters non-commitments, resolves owners and dates, and scores retained actions.</summary>
    public static AnalysisResult Process(
        IEnumerable<ActionItemCandidate> candidates,
        string transcript,
        DateTime meetingDate,
        IReadOnlyList<string> attendees,
        IReadOnlyList<string>? decisions = null,
        string provider = "Stub",
        string model = "Built-in rules",
        long elapsedMilliseconds = 0,
        int chunkCount = 1,
        IReadOnlyList<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(attendees);

        var accepted = new List<ActionItem>();
        var finalDecisions = decisions?.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).ToList() ?? [];
        var droppedQuoteCount = 0;
        foreach (var candidate in candidates)
        {
            var item = candidate.Item;
            if (!ContainsVerifiedQuote(transcript, item.SourceQuote))
            {
                droppedQuoteCount++;
                continue;
            }

            var hasHedge = HedgePattern.IsMatch(item.SourceQuote);
            if (!candidate.IsCommitment || hasHedge)
            {
                finalDecisions.Add(item.SourceQuote);
                continue;
            }

            ResolveOwner(item, attendees);
            if (!string.IsNullOrWhiteSpace(item.DueDateText)
                && !ContainsVerifiedQuote(transcript, item.DueDateText))
            {
                item.DueDateText = string.Empty;
            }

            item.DueDate = DueDateResolver.Resolve(item.DueDateText, meetingDate);
            item.NeedsInput.RemoveAll(value =>
                value.Equals("owner", StringComparison.OrdinalIgnoreCase)
                || value.Equals("due date", StringComparison.OrdinalIgnoreCase)
                || value.Equals("speaker unknown", StringComparison.OrdinalIgnoreCase));
            if (item.OwnerName is null)
            {
                item.NeedsInput.Add("owner");
                if (StartsWithFirstPersonCommitment(item.SourceQuote))
                {
                    item.NeedsInput.Add("speaker unknown");
                }
            }

            if (item.DueDate is null)
            {
                item.NeedsInput.Add("due date");
            }

            item.Confidence = ScoreConfidence(item.Confidence, item.OwnerName is not null, item.DueDate is not null, hasHedge);
            if (!accepted.Any(existing => AreNearDuplicates(existing, item)))
            {
                accepted.Add(item);
            }
        }

        var uniqueDecisions = finalDecisions
            .DistinctBy(NormalizeWhitespaceAndCase, StringComparer.Ordinal)
            .ToArray();
        return new AnalysisResult(
            accepted,
            uniqueDecisions,
            droppedQuoteCount,
            provider,
            model,
            Math.Max(0, elapsedMilliseconds),
            Math.Max(1, chunkCount),
            warnings);
    }

    /// <summary>Matches a proposed owner to an attendee, preserving non-attendees only as suggestions.</summary>
    private static void ResolveOwner(ActionItem item, IReadOnlyList<string> attendees)
    {
        var quoteOwner = ExtractSpeakerOwner(item.SourceQuote);
        var proposedName = !string.IsNullOrWhiteSpace(quoteOwner)
            ? quoteOwner
            : !string.IsNullOrWhiteSpace(item.OwnerName)
            ? item.OwnerName
            : !string.IsNullOrWhiteSpace(item.SuggestedOwnerName)
                ? item.SuggestedOwnerName
                : null;
        if (string.IsNullOrWhiteSpace(proposedName)
            || IsGenericOwner(proposedName))
        {
            item.OwnerName = null;
            item.SuggestedOwnerName = null;
            return;
        }

        var attendee = MatchAttendee(proposedName, attendees);
        item.OwnerName = attendee;
        item.SuggestedOwnerName = attendee is null ? proposedName.Trim() : null;
    }

    /// <summary>Matches normalized, honorific-free owner names to exactly one attendee.</summary>
    private static string? MatchAttendee(string proposedName, IReadOnlyList<string> attendees)
    {
        var proposedTokens = GetNameTokens(proposedName);
        if (proposedTokens.Length == 0)
        {
            return null;
        }

        var proposedFull = TextVariants(string.Join(' ', proposedTokens));
        var exactFullMatches = attendees
            .Select((attendee, index) => (attendee, index))
            .Where(pair => NameFormsOverlap(proposedFull, TextVariants(string.Join(' ', GetNameTokens(pair.attendee)))))
            .Select(pair => pair.index)
            .ToArray();
        if (exactFullMatches.Length > 0)
        {
            return exactFullMatches.Length == 1 ? attendees[exactFullMatches[0]] : null;
        }

        var proposedFirst = ExpandNicknames(TextVariants(proposedTokens[0]));
        var firstNameMatches = attendees
            .Select((attendee, index) => (attendee, index))
            .Where(pair =>
            {
                var attendeeTokens = GetNameTokens(pair.attendee);
                return attendeeTokens.Length > 0
                    && NameFormsOverlap(proposedFirst, ExpandNicknames(TextVariants(attendeeTokens[0])));
            })
            .Select(pair => pair.index)
            .ToArray();
        return firstNameMatches.Length == 1 ? attendees[firstNameMatches[0]] : null;
    }

    /// <summary>Creates normalized name tokens after removing leading honorifics and trailing suffixes.</summary>
    private static string[] GetNameTokens(string name)
    {
        var normalized = name.Normalize(NormalizationForm.FormC)
            .Replace("ma'am", "maam", StringComparison.OrdinalIgnoreCase);
        var tokens = WordPattern.Matches(normalized)
            .Select(match => match.Value.ToLowerInvariant())
            .ToList();
        while (tokens.Count > 0 && Honorifics.Contains(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        while (tokens.Count > 0 && Honorifics.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return tokens.ToArray();
    }

    /// <summary>Returns the original normalized spelling and its Devanagari-to-Roman comparison form.</summary>
    private static HashSet<string> TextVariants(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var variants = new HashSet<string>(StringComparer.Ordinal) { normalized };
        var romanized = TransliterateDevanagari(normalized);
        if (romanized.Length > 0)
        {
            variants.Add(romanized);
        }

        return variants;
    }

    /// <summary>Expands known common nicknames without changing the displayed attendee name.</summary>
    private static HashSet<string> ExpandNicknames(HashSet<string> variants)
    {
        var expanded = new HashSet<string>(variants, StringComparer.Ordinal);
        foreach (var variant in variants)
        {
            if (NicknameAliases.TryGetValue(variant, out var aliases))
            {
                expanded.UnionWith(aliases);
            }
        }

        return expanded;
    }

    /// <summary>Compares names exactly first, then permits a single edit for likely speech-recognition noise.</summary>
    private static bool NameFormsOverlap(HashSet<string> first, HashSet<string> second)
    {
        if (first.Overlaps(second))
        {
            return true;
        }

        return first.Any(left => second.Any(right =>
            Math.Min(left.Length, right.Length) >= 4 && WithinOneCharacterEdit(left, right)));
    }

    /// <summary>Transliterates common Devanagari letters and vowel signs for attendee-name comparison only.</summary>
    private static string TransliterateDevanagari(string value)
    {
        var output = new StringBuilder();
        var hasInherentVowel = false;
        foreach (var character in value)
        {
            if (DevanagariConsonants.TryGetValue(character, out var consonant))
            {
                // Devanagari consonants have an inherent "a" unless a vowel sign or virama replaces it.
                output.Append(consonant).Append('a');
                hasInherentVowel = true;
            }
            else if (DevanagariVowelSigns.TryGetValue(character, out var vowelSign))
            {
                RemovePendingInherentVowel(output, hasInherentVowel);
                output.Append(vowelSign);
                hasInherentVowel = false;
            }
            else if (character == '्')
            {
                RemovePendingInherentVowel(output, hasInherentVowel);
                hasInherentVowel = false;
            }
            else if (DevanagariVowels.TryGetValue(character, out var vowel))
            {
                output.Append(vowel);
                hasInherentVowel = false;
            }
            else if (character is 'ं' or 'ँ')
            {
                output.Append('n');
                hasInherentVowel = false;
            }
            else if (character == 'ः')
            {
                output.Append('h');
                hasInherentVowel = false;
            }
            else if (character is '़' or '\u200c' or '\u200d')
            {
                continue;
            }
            else if (char.IsWhiteSpace(character))
            {
                RemovePendingInherentVowel(output, hasInherentVowel);
                if (output.Length > 0 && output[^1] != ' ')
                {
                    output.Append(' ');
                }

                hasInherentVowel = false;
            }
            else if (char.IsLetterOrDigit(character))
            {
                output.Append(character);
                hasInherentVowel = false;
            }
            else if (output.Length > 0 && output[^1] != ' ')
            {
                output.Append(' ');
                hasInherentVowel = false;
            }
        }

        RemovePendingInherentVowel(output, hasInherentVowel);
        return Regex.Replace(output.ToString().Trim(), "([aiu])\\1+", "$1");
    }

    /// <summary>Removes the implicit vowel written after a Devanagari consonant when a sign suppresses it.</summary>
    private static void RemovePendingInherentVowel(StringBuilder output, bool pending)
    {
        if (pending && output.Length > 0 && output[^1] == 'a')
        {
            output.Length--;
        }
    }

    /// <summary>Extracts a direct-request or named-commitment speaker from one source line.</summary>
    private static string? ExtractSpeakerOwner(string quote)
    {
        var line = TimestampPrefixPattern.Replace(quote, string.Empty);
        var match = SpeakerOwnerPattern.Match(line);
        return match.Success ? match.Groups["name"].Value.Trim() : null;
    }

    /// <summary>Prevents vague people, teams, and roles from being treated as named attendees.</summary>
    private static bool IsGenericOwner(string name)
    {
        var normalized = string.Join(' ', GetNameTokens(name));
        return normalized is "someone" or "somebody" or "we" or "everyone" or "team"
            or "koi" or "kisi ko" or "hum log" or "sab log"
            || normalized.EndsWith(" team", StringComparison.Ordinal)
            || normalized is "qa" or "backend" or "platform";
    }

    /// <summary>Identifies first-person commitments whose speaker may come from the transcript line label.</summary>
    private static bool StartsWithFirstPersonCommitment(string quote) =>
        FirstPersonCommitmentPattern.IsMatch(TimestampPrefixPattern.Replace(quote, string.Empty));

    /// <summary>Combines model confidence with verified evidence and deterministic owner/date signals.</summary>
    private static double ScoreConfidence(double modelConfidence, bool ownerResolved, bool dateResolved, bool hasHedge)
    {
        var score = Math.Clamp(modelConfidence, 0, 1) * 0.5
            + 0.25
            + (ownerResolved ? 0.1 : -0.2)
            + (dateResolved ? 0.1 : -0.2)
            - (hasHedge ? 0.05 : 0);
        return Math.Clamp(score, 0, 0.95);
    }

    /// <summary>Checks normalized substring evidence and allows at most one token-level character edit.</summary>
    private static bool ContainsVerifiedQuote(string transcript, string quote)
    {
        if (string.IsNullOrWhiteSpace(quote))
        {
            return false;
        }

        var normalizedTranscript = NormalizeQuoteForComparison(transcript);
        var normalizedQuote = NormalizeQuoteForComparison(quote);
        if (normalizedQuote.Length == 0)
        {
            return false;
        }

        if (normalizedTranscript.Contains(normalizedQuote, StringComparison.Ordinal))
        {
            return true;
        }

        var transcriptWords = WordPattern.Matches(normalizedTranscript).Select(match => match.Value).ToArray();
        var quoteWords = WordPattern.Matches(normalizedQuote).Select(match => match.Value).ToArray();
        if (quoteWords.Length < 4 || transcriptWords.Length < quoteWords.Length)
        {
            return false;
        }

        for (var start = 0; start <= transcriptWords.Length - quoteWords.Length; start++)
        {
            var edits = 0;
            for (var index = 0; index < quoteWords.Length; index++)
            {
                if (transcriptWords[start + index].Equals(quoteWords[index], StringComparison.Ordinal))
                {
                    continue;
                }

                if (!WithinOneCharacterEdit(transcriptWords[start + index], quoteWords[index])
                    || ++edits > 1)
                {
                    edits = 2;
                    break;
                }
            }

            if (edits <= 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tests whether two words differ by at most one character insertion, deletion, or substitution.</summary>
    private static bool WithinOneCharacterEdit(string first, string second)
    {
        if (Math.Abs(first.Length - second.Length) > 1)
        {
            return false;
        }

        var firstIndex = 0;
        var secondIndex = 0;
        var edits = 0;
        while (firstIndex < first.Length && secondIndex < second.Length)
        {
            if (first[firstIndex] == second[secondIndex])
            {
                firstIndex++;
                secondIndex++;
                continue;
            }

            if (++edits > 1)
            {
                return false;
            }

            if (first.Length == second.Length)
            {
                firstIndex++;
                secondIndex++;
            }
            else if (first.Length > second.Length)
            {
                firstIndex++;
            }
            else
            {
                secondIndex++;
            }
        }

        return edits + (firstIndex < first.Length || secondIndex < second.Length ? 1 : 0) <= 1;
    }

    /// <summary>Identifies duplicates using the normalized quote-and-owner identity key.</summary>
    private static bool AreNearDuplicates(ActionItem first, ActionItem second) =>
        string.Equals(
            NormalizeQuoteForComparison(first.SourceQuote),
            NormalizeQuoteForComparison(second.SourceQuote),
            StringComparison.Ordinal)
        && string.Equals(
            NormalizeOwnerKey(first.OwnerName),
            NormalizeOwnerKey(second.OwnerName),
            StringComparison.Ordinal);

    /// <summary>Normalizes an owner key without altering the display name returned by the analysis.</summary>
    private static string NormalizeOwnerKey(string? ownerName) =>
        string.Join(' ', ownerName is null ? [] : GetNameTokens(ownerName));

    /// <summary>Normalizes Unicode, punctuation, whitespace, casing, and known filler words without translation.</summary>
    private static string NormalizeQuoteForComparison(string value)
    {
        var tokens = WordPattern.Matches(value.Normalize(NormalizationForm.FormC))
            .Select(match => match.Value.ToLowerInvariant())
            .ToArray();
        var normalized = new List<string>(tokens.Length);
        for (var index = 0; index < tokens.Length;)
        {
            var fillerLength = FillerPhrases
                .FirstOrDefault(phrase => MatchesTokens(tokens, index, phrase))
                ?.Length ?? 0;
            if (fillerLength > 0)
            {
                index += fillerLength;
            }
            else if (FillerWords.Contains(tokens[index]))
            {
                index++;
            }
            else
            {
                normalized.Add(tokens[index]);
                index++;
            }
        }

        return string.Join(' ', normalized);
    }

    /// <summary>Checks whether a filler phrase begins at a token offset without changing transcript text.</summary>
    private static bool MatchesTokens(IReadOnlyList<string> tokens, int offset, IReadOnlyList<string> phrase)
    {
        if (offset + phrase.Count > tokens.Count)
        {
            return false;
        }

        for (var index = 0; index < phrase.Count; index++)
        {
            if (!tokens[offset + index].Equals(phrase[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Normalizes whitespace and case for quote, decision, and duplicate comparisons.</summary>
    private static string NormalizeWhitespaceAndCase(string value) =>
        string.Join(' ', value.Normalize(NormalizationForm.FormC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}
