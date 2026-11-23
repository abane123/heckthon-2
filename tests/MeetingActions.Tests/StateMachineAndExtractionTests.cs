using FluentAssertions;
using MeetingActions.Core.Domain;
using MeetingActions.Infrastructure.Integrations;

namespace MeetingActions.Tests;

/// <summary>Verifies workflow transitions and transcript extraction safeguards.</summary>
public sealed class StateMachineAndExtractionTests
{
    /// <summary>Allows documented workflow transitions and rejects invalid moves.</summary>
    [Fact]
    public void StateMachineAllowsAndRejectsTransitions()
    {
        var stateMachine = new ActionItemStateMachine();
        stateMachine.CanTransition(ActionStatus.Draft, ActionStatus.Approved).Should().BeTrue();
        stateMachine.CanTransition(ActionStatus.Draft, ActionStatus.Done).Should().BeFalse();
        stateMachine.CanTransition(ActionStatus.Approved, ActionStatus.Draft).Should().BeTrue();
        stateMachine.CanTransition(ActionStatus.Rejected, ActionStatus.Draft).Should().BeTrue();
        stateMachine.CanTransition(ActionStatus.Done, ActionStatus.Draft).Should().BeFalse();
        var item = new ActionItem { Status = ActionStatus.Draft };

        stateMachine.Transition(item, ActionStatus.Approved);
        item.Status.Should().Be(ActionStatus.Approved);
        var transition = () => stateMachine.Transition(item, ActionStatus.Rejected);
        transition.Should().Throw<InvalidOperationException>();
    }

    /// <summary>Drops unsupported candidates and flags candidates missing an owner.</summary>
    [Fact]
    public void ExtractionRulesDropMissingQuotesAndFlagMissingOwner()
    {
        var supported = new ActionItem
        {
            Title = "Review limits",
            SourceQuote = "Someone should review the limits.",
            Confidence = 0.4
        };
        var unsupported = new ActionItem { Title = "Invented task", SourceQuote = "" };

        var result = ActionItemExtractionRules.Apply([supported, unsupported]);

        result.Should().ContainSingle().Which.Should().BeSameAs(supported);
        supported.NeedsInput.Should().Contain("owner").And.Contain("due date");
    }

    /// <summary>Extracts a case-insensitive attendee owner from a direct request.</summary>
    [Fact]
    public async Task ExtractorResolvesCanYouSpeakerAgainstAttendees()
    {
        var extractor = new StubLlmExtractor();

        var result = await extractor.ExtractAsync(
            "PRIYA, can you sort it before the release?", new DateTime(2026, 11, 17), ["Priya"]);

        result.Should().ContainSingle();
        result[0].OwnerName.Should().Be("Priya");
        result[0].NeedsInput.Should().Contain("due date");
        result[0].Title.Should().Be("Sort it before the release");

        var contextualResult = await extractor.ExtractAsync(
            "Priya, can you sort it before the release? The retry behavior is also a little unclear.",
            new DateTime(2026, 11, 17), ["Priya"]);
        contextualResult.Single().Title.Should().Be("Sort the retry behavior before the release");
    }

    /// <summary>Resolves the end-of-next-week phrase to the following Friday.</summary>
    [Fact]
    public async Task ExtractorResolvesEndOfNextWeekRelativeToMeetingDate()
    {
        var extractor = new StubLlmExtractor();

        var result = await extractor.ExtractAsync(
            "Priya, can you finish the report by end of next week?", new DateTime(2026, 11, 17), ["Priya"]);

        result.Single().DueDate.Should().Be(new DateTime(2026, 11, 27));
        result.Single().DueDateText.Should().Be("by end of next week");
    }

    /// <summary>Drops past-discussion lines that hedge an action as uncertain.</summary>
    [Fact]
    public async Task ExtractorDropsHedgedPastDiscussion()
    {
        var extractor = new StubLlmExtractor();

        var result = await extractor.ExtractAsync(
            "We said the documentation should be ready by end of next week, but that is not a firm date",
            new DateTime(2026, 11, 17), ["Priya"]);

        result.Should().BeEmpty();
    }

    /// <summary>Scores fully resolved actions high and vague unowned actions low.</summary>
    [Fact]
    public async Task ExtractorAssignsConfidenceScoresAndDraftStatus()
    {
        var extractor = new StubLlmExtractor();
        var result = await extractor.ExtractAsync(
            "Priya, can you review the limits by 2026-11-20?\nSomeone should look at the rate limits.",
            new DateTime(2026, 11, 17), ["Priya"]);

        result.Should().HaveCount(2);
        result[0].Status.Should().Be(ActionStatus.Draft);
        result[0].Confidence.Should().Be(0.95);
        result[0].Confidence.Should().BeGreaterThanOrEqualTo(0.75);
        result[1].OwnerName.Should().BeNull();
        result[1].NeedsInput.Should().Contain("owner");
        result[1].Confidence.Should().Be(0.3);
        result[1].Confidence.Should().BeLessThan(0.5);
    }

    /// <summary>Marks an unrecognized named speaker and speakerless tentative commitment as needing input.</summary>
    [Fact]
    public async Task ExtractorFlagsUnknownSpeakerAndTentativeCommitment()
    {
        var extractor = new StubLlmExtractor();

        var result = await extractor.ExtractAsync(
            "Morgan, can you check the cache by Friday?\nI can ask the platform team about the alert thresholds.",
            new DateTime(2026, 11, 17), ["Priya"]);

        result.Should().HaveCount(2);
        result[0].OwnerName.Should().BeNull();
        result[0].NeedsInput.Should().Contain("owner").And.Contain("speaker unknown");
        result[1].Confidence.Should().BeLessThan(0.5);
        result[1].NeedsInput.Should().Contain("speaker unknown");

        var commitmentResult = await extractor.ExtractAsync(
            "alex will write down the rollout checks so support knows what to watch.\nWe will check the rate limits by Friday.",
            new DateTime(2026, 11, 17), ["Alex"]);
        commitmentResult.Should().HaveCount(2);
        commitmentResult[0].OwnerName.Should().Be("Alex");
        commitmentResult[0].Title.Should().Be("Write down the rollout checks");
        commitmentResult[1].OwnerName.Should().BeNull();
        commitmentResult[1].NeedsInput.Should().Contain("owner");
    }
}
