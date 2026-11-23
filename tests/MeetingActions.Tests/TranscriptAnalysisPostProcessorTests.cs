using FluentAssertions;
using MeetingActions.Core.Domain;
using MeetingActions.Core.Services;

namespace MeetingActions.Tests;

/// <summary>Verifies evidence, owner, date, commitment, and confidence post-processing rules.</summary>
public sealed class TranscriptAnalysisPostProcessorTests
{
    /// <summary>Resolves the new Hindi and Hinglish relative-day phrases from the meeting date.</summary>
    [Theory]
    [InlineData("kal tak", 18)]
    [InlineData("कल तक", 18)]
    [InlineData("aaj", 17)]
    [InlineData("आज", 17)]
    [InlineData("parson tak", 19)]
    [InlineData("परसों तक", 19)]
    public void ResolvesHindiRelativeDays(string phrase, int expectedDay)
    {
        DueDateResolver.Resolve(phrase, new DateTime(2026, 11, 17))
            .Should().Be(new DateTime(2026, 11, expectedDay));
    }

    /// <summary>Resolves English, Roman Hindi, and Devanagari weekday names with their "tak" forms.</summary>
    [Theory]
    [InlineData("Monday tak", 23)]
    [InlineData("somvar tak", 23)]
    [InlineData("सोमवार तक", 23)]
    [InlineData("Tuesday tak", 24)]
    [InlineData("mangalvar tak", 24)]
    [InlineData("मंगलवार तक", 24)]
    [InlineData("Wednesday tak", 18)]
    [InlineData("budhvar tak", 18)]
    [InlineData("बुधवार तक", 18)]
    [InlineData("Thursday tak", 19)]
    [InlineData("guruvar tak", 19)]
    [InlineData("गुरुवार तक", 19)]
    [InlineData("Friday tak", 20)]
    [InlineData("shukravar tak", 20)]
    [InlineData("शुक्रवार तक", 20)]
    [InlineData("Saturday tak", 21)]
    [InlineData("shanivar tak", 21)]
    [InlineData("शनिवार तक", 21)]
    [InlineData("Sunday tak", 22)]
    [InlineData("ravivar tak", 22)]
    [InlineData("रविवार तक", 22)]
    public void ResolvesWeekdayAliases(string phrase, int expectedDay)
    {
        DueDateResolver.Resolve(phrase, new DateTime(2026, 11, 17))
            .Should().Be(new DateTime(2026, 11, expectedDay));
    }

    /// <summary>Resolves week-end, month-end, EOD, COB, and ordinal-day expressions.</summary>
    [Theory]
    [InlineData("agle hafte", 2026, 11, 27)]
    [InlineData("अगले हफ्ते तक", 2026, 11, 27)]
    [InlineData("is hafte ke end tak", 2026, 11, 20)]
    [InlineData("इस हफ्ते के अंत तक", 2026, 11, 20)]
    [InlineData("mahine ke end", 2026, 11, 30)]
    [InlineData("महीने के अंत तक", 2026, 11, 30)]
    [InlineData("EOD", 2026, 11, 17)]
    [InlineData("COB", 2026, 11, 17)]
    [InlineData("by the 27th", 2026, 11, 27)]
    [InlineData("by the 17th", 2026, 11, 17)]
    public void ResolvesWeekMonthAndOrdinalDeadlines(
        string phrase,
        int year,
        int month,
        int day)
    {
        DueDateResolver.Resolve(phrase, new DateTime(2026, 11, 17))
            .Should().Be(new DateTime(year, month, day));
    }

    /// <summary>Uses the next calendar month when the requested ordinal day has already passed.</summary>
    [Fact]
    public void ResolvesOrdinalDayToNextOccurrence()
    {
        DueDateResolver.Resolve("by the 27th", new DateTime(2026, 11, 28))
            .Should().Be(new DateTime(2026, 12, 27));
    }

    /// <summary>Leaves vague or release-dependent phrases unresolved instead of guessing a calendar date.</summary>
    [Theory]
    [InlineData("ASAP")]
    [InlineData("jaldi se jaldi")]
    [InlineData("soon")]
    [InlineData("baad mein")]
    [InlineData("before the release")]
    public void DoesNotResolveVagueDeadlinePhrases(string phrase)
    {
        DueDateResolver.Resolve(phrase, new DateTime(2026, 11, 17)).Should().BeNull();
    }

    /// <summary>Retains the transcript's original unsupported deadline phrase and requests organizer input.</summary>
    [Fact]
    public void PreservesUnresolvedDueDateTextAndNeedsInput()
    {
        const string quote = "Priya will review the release checklist ASAP.";
        var result = Process(
            quote,
            Candidate(quote, "Review release checklist", "Priya", "ASAP"),
            ["Priya"]);

        result.Items.Single().DueDate.Should().BeNull();
        result.Items.Single().DueDateText.Should().Be("ASAP");
        result.Items.Single().NeedsInput.Should().Contain("due date");
    }

    /// <summary>Resolves an exact date phrase on a neighboring transcript line without changing its wording.</summary>
    [Fact]
    public void PreservesVerbatimDeadlineFromNeighboringLine()
    {
        const string transcript = "Priya will review the release checklist.\nBy Friday!";
        var result = Process(
            transcript,
            Candidate("Priya will review the release checklist.", "Review release checklist", "Priya", "By Friday"),
            ["Priya"]);

        result.Items.Single().DueDate.Should().Be(new DateTime(2026, 11, 20));
        result.Items.Single().DueDateText.Should().Be("By Friday");
    }

    /// <summary>Drops a proposal whose source quote is not present and counts the unsupported item.</summary>
    [Fact]
    public void DropsHallucinatedQuoteAndIncrementsDroppedCount()
    {
        var result = Process(
            "Priya will review the release checklist by Friday.",
            Candidate("Priya will deploy the release by Friday.", "Deploy release", "Priya", "by Friday"),
            ["Priya"]);

        result.Items.Should().BeEmpty();
        result.DroppedCount.Should().Be(1);
    }

    /// <summary>Keeps a named non-attendee as a suggestion instead of assigning that owner.</summary>
    [Fact]
    public void DoesNotAssignOwnerOutsideAttendeeList()
    {
        const string quote = "Jordan will review the release checklist by Friday.";
        var result = Process(
            quote,
            Candidate(quote, "Review release checklist", "Priya", "by Friday"),
            ["Priya"]);

        result.Items.Should().ContainSingle();
        result.Items[0].OwnerName.Should().BeNull();
        result.Items[0].SuggestedOwnerName.Should().Be("Jordan");
        result.Items[0].NeedsInput.Should().Contain("owner");
    }

    /// <summary>Resolves the original due phrase in code rather than trusting a proposed final date.</summary>
    [Fact]
    public void ResolvesEndOfNextWeekFromMeetingDateInsteadOfModelDate()
    {
        const string quote = "Priya will complete the report by end of next week.";
        var candidate = Candidate(quote, "Complete report", "Priya", "end of next week");
        candidate.Item.DueDate = new DateTime(2026, 11, 18);

        var result = Process(quote, candidate, ["Priya"]);

        result.Items.Single().DueDate.Should().Be(new DateTime(2026, 11, 27));
        result.Items.Single().DueDateText.Should().Be("end of next week");
    }

    /// <summary>Moves a verified hedged line to decisions and does not retain it as an action.</summary>
    [Fact]
    public void FiltersHedgedLineFromActionItems()
    {
        const string quote = "Maybe Priya should review the release checklist.";
        var result = Process(
            quote,
            Candidate(quote, "Review release checklist", "Priya", null),
            ["Priya"]);

        result.Items.Should().BeEmpty();
        result.Decisions.Should().Contain(quote);
        result.DroppedCount.Should().Be(0);
    }

    /// <summary>Accepts a quote with only a small word typo and scores resolved versus incomplete items.</summary>
    [Fact]
    public void AllowsSmallQuoteToleranceAndAppliesConfidenceTiers()
    {
        const string transcript = "Priya will review the limit by Friday.\nI can ask about the platform alert thresholds.";
        var verified = Candidate("Priya will review the limits by Friday.", "Review limits", "priya", "by Friday");
        var incomplete = Candidate(
            "I can ask about the platform alert thresholds.",
            "Ask about alert thresholds",
            null,
            null);

        var result = Process(transcript, [verified, incomplete], ["Priya"]);

        result.Items.Should().HaveCount(2);
        result.Items[0].Confidence.Should().Be(0.95);
        result.Items[1].Confidence.Should().BeApproximately(0.35, 0.000001);
        result.Items[0].DueDate.Should().Be(new DateTime(2026, 11, 20));
        result.Items[1].NeedsInput.Should().Contain("owner").And.Contain("due date");
    }

    /// <summary>Matches first/full names after honorific and suffix removal, including accented Unicode forms.</summary>
    [Theory]
    [InlineData("Priya ji", "Priya Sharma")]
    [InlineData("Priya Sharma sir", "Priya Sharma")]
    [InlineData("madam Priya Sharma", "Priya Sharma")]
    [InlineData("Mr. Priya Sharma", "Priya Sharma")]
    [InlineData("Ms. Priya Sharma", "Priya Sharma")]
    [InlineData("Priya Sharma ma'am", "Priya Sharma")]
    [InlineData("Priya Sharma bhai", "Priya Sharma")]
    [InlineData("Priya Sharma bhaiya", "Priya Sharma")]
    [InlineData("Priya Sharma didi", "Priya Sharma")]
    [InlineData("Priya Sharma saab", "Priya Sharma")]
    [InlineData("Priya Sharma sahab", "Priya Sharma")]
    [InlineData("Jose\u0301", "José Alvarez")]
    public void MatchesNormalizedOwnerNameWithHonorificsAndSuffixes(string proposedOwner, string attendee)
    {
        const string quote = "Please review the release checklist.";
        var result = Process(
            quote,
            Candidate(quote, "Review release checklist", proposedOwner, null),
            [attendee]);

        result.Items.Single().OwnerName.Should().Be(attendee);
    }

    /// <summary>Matches known nicknames and speech-recognition variants against attendee first names.</summary>
    [Theory]
    [InlineData("Alex", "Alexander Kim")]
    [InlineData("Mike", "Michael Chen")]
    [InlineData("Priyaa", "Priya Sharma")]
    public void MatchesNicknameAndOneEditNameVariants(string proposedOwner, string attendee)
    {
        const string quote = "Please review the release checklist.";
        var result = Process(
            quote,
            Candidate(quote, "Review release checklist", proposedOwner, null),
            [attendee]);

        result.Items.Single().OwnerName.Should().Be(attendee);
    }

    /// <summary>Matches Devanagari attendee names to their Roman-script attendee spelling.</summary>
    [Fact]
    public void MatchesDevanagariOwnerToRomanAttendeeName()
    {
        const string quote = "प्रिया, कल तक रिपोर्ट भेज देना।";
        var result = Process(
            quote,
            Candidate(quote, "Send the report", "प्रिया", "कल तक"),
            ["Priya Sharma"]);

        result.Items.Single().OwnerName.Should().Be("Priya Sharma");
        result.Items.Single().DueDate.Should().Be(new DateTime(2026, 11, 18));
    }

    /// <summary>Transliterates a full Devanagari name before matching the attendee's full Roman spelling.</summary>
    [Fact]
    public void MatchesFullDevanagariNameToRomanAttendeeName()
    {
        const string quote = "अमित, कल तक रिपोर्ट भेज देना।";
        var result = Process(
            quote,
            Candidate(quote, "Send the report", "अमित वर्मा", "कल तक"),
            ["Amit Verma"]);

        result.Items.Single().OwnerName.Should().Be("Amit Verma");
    }

    /// <summary>Refuses a first-name match when multiple attendees share that first name.</summary>
    [Fact]
    public void LeavesAmbiguousFirstNameUnassigned()
    {
        const string quote = "Please review the release checklist.";
        var result = Process(
            quote,
            Candidate(quote, "Review release checklist", "Priya", null),
            ["Priya Sharma", "Priya Verma"]);

        result.Items.Single().OwnerName.Should().BeNull();
        result.Items.Single().NeedsInput.Should().Contain("owner");
    }

    /// <summary>Verifies evidence after NFC, punctuation, case, whitespace, and filler normalization without translation.</summary>
    [Fact]
    public void NormalizesQuoteEvidenceAndNeverTranslatesIt()
    {
        const string transcript = "Um, Cafe\u0301! Priya, review the release checklist by Friday, you know.";
        var normalizedQuote = Candidate(
            "Café Priya review the release checklist by Friday",
            "Review the release checklist",
            "Priya",
            "by Friday");
        var translatedQuote = Candidate(
            "प्रिया रिपोर्ट भेजें",
            "Send the report",
            "Priya",
            null);

        var result = Process(transcript, [normalizedQuote, translatedQuote], ["Priya"]);

        result.Items.Should().ContainSingle()
            .Which.SourceQuote.Should().Be("Café Priya review the release checklist by Friday");
        result.DroppedCount.Should().Be(1);
    }

    /// <summary>Deduplicates by normalized quote and owner while preserving other owners and distinct evidence.</summary>
    [Fact]
    public void DeduplicatesOnlyWhenNormalizedQuoteAndOwnerBothMatch()
    {
        const string transcript = "Review the agenda by Friday.\nReview the agenda item by Friday.";
        var firstPriya = Candidate("Review the agenda by Friday.", "Publish the agenda", "Priya", "by Friday");
        var punctuationDuplicate = Candidate(
            "um, review the agenda by Friday!",
            "Other title",
            "Priya ji",
            "by Friday");
        var sameQuoteOtherOwner = Candidate(
            "Review the agenda by Friday.",
            "Publish the agenda",
            "Alex",
            "by Friday");
        var sameOwnerDifferentQuote = Candidate(
            "Review the agenda item by Friday.",
            "Publish the agenda",
            "Priya",
            "by Friday");

        var result = Process(
            transcript,
            [firstPriya, punctuationDuplicate, sameQuoteOtherOwner, sameOwnerDifferentQuote],
            ["Priya", "Alex"]);

        result.Items.Should().HaveCount(3);
        result.Items.Select(item => item.OwnerName).Should().Contain(["Priya", "Alex"]);
        result.DroppedCount.Should().Be(0);
    }

    /// <summary>Builds a proposal with a fixed model confidence for focused post-processing assertions.</summary>
    private static ActionItemCandidate Candidate(
        string quote,
        string title,
        string? owner,
        string? dueDateText) =>
        new(new ActionItem
        {
            Title = title,
            OwnerName = owner,
            DueDateText = dueDateText ?? string.Empty,
            Confidence = 1,
            SourceQuote = quote
        }, true);

    /// <summary>Runs a single proposal through the production analysis rules.</summary>
    private static AnalysisResult Process(
        string transcript,
        ActionItemCandidate candidate,
        IReadOnlyList<string> attendees) =>
        Process(transcript, [candidate], attendees);

    /// <summary>Runs multiple proposals against the fixed meeting date used by these rule tests.</summary>
    private static AnalysisResult Process(
        string transcript,
        IReadOnlyList<ActionItemCandidate> candidates,
        IReadOnlyList<string> attendees) =>
        ActionItemPostProcessor.Process(
            candidates,
            transcript,
            new DateTime(2026, 11, 17),
            attendees,
            provider: "Ollama",
            model: "test-model",
            elapsedMilliseconds: 12,
            chunkCount: 1);
}
