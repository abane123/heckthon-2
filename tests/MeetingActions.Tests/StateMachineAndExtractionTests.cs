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

        result.Items.Should().ContainSingle();
        result.Items[0].OwnerName.Should().Be("Priya");
        result.Items[0].NeedsInput.Should().Contain("due date");
        result.Items[0].Title.Should().Be("Sort it before the release");

        var contextualResult = await extractor.ExtractAsync(
            "Priya, can you sort it before the release? The retry behavior is also a little unclear.",
            new DateTime(2026, 11, 17), ["Priya"]);
        contextualResult.Items.Single().Title.Should().Be("Sort the retry behavior before the release");
    }

    /// <summary>Resolves the end-of-next-week phrase to the following Friday.</summary>
    [Fact]
    public async Task ExtractorResolvesEndOfNextWeekRelativeToMeetingDate()
    {
        var extractor = new StubLlmExtractor();

        var result = await extractor.ExtractAsync(
            "Priya, can you finish the report by end of next week?", new DateTime(2026, 11, 17), ["Priya"]);

        result.Items.Single().DueDate.Should().Be(new DateTime(2026, 11, 27));
        result.Items.Single().DueDateText.Should().Be("by end of next week");
    }

    /// <summary>Preserves the "by" preposition when extracting an explicit due-date phrase.</summary>
    [Fact]
    public async Task ExtractorPreservesByPrefixOnExplicitDueDate()
    {
        var result = await new StubLlmExtractor().ExtractAsync(
            "Mina will prepare the migration checklist by 2026-11-25.",
            new DateTime(2026, 11, 18),
            ["Mina"]);

        result.Items.Should().ContainSingle();
        result.Items.Single().DueDateText.Should().Be("by 2026-11-25");
        result.Items.Single().DueDate.Should().Be(new DateTime(2026, 11, 25));
    }

    /// <summary>Drops past-discussion lines that hedge an action as uncertain.</summary>
    [Fact]
    public async Task ExtractorDropsHedgedPastDiscussion()
    {
        var extractor = new StubLlmExtractor();

        var result = await extractor.ExtractAsync(
            "We said the documentation should be ready by end of next week, but that is not a firm date",
            new DateTime(2026, 11, 17), ["Priya"]);

        result.Items.Should().BeEmpty();
    }

    /// <summary>Scores fully resolved actions high and vague unowned actions low.</summary>
    [Fact]
    public async Task ExtractorAssignsConfidenceScoresAndDraftStatus()
    {
        var extractor = new StubLlmExtractor();
        var result = await extractor.ExtractAsync(
            "Priya, can you review the limits by 2026-11-20?\nI can ask the platform team about the alert thresholds.",
            new DateTime(2026, 11, 17), ["Priya"]);

        result.Items.Should().HaveCount(2);
        result.Items[0].Status.Should().Be(ActionStatus.Draft);
        result.Items[0].Confidence.Should().Be(0.95);
        result.Items[0].Confidence.Should().BeGreaterThanOrEqualTo(0.75);
        result.Items[1].OwnerName.Should().BeNull();
        result.Items[1].NeedsInput.Should().Contain("owner");
        result.Items[1].Confidence.Should().BeApproximately(0.35, 0.000001);
        result.Items[1].Confidence.Should().BeLessThan(0.5);
    }

    /// <summary>Drops vague "someone should" suggestions instead of treating them as commitments.</summary>
    [Fact]
    public async Task ExtractorDropsVagueSomeoneShouldSuggestions()
    {
        var result = await new StubLlmExtractor().ExtractAsync(
            "Someone should look at the rate limits.",
            new DateTime(2026, 11, 17),
            ["Priya"]);

        result.Items.Should().BeEmpty();
    }

    /// <summary>Suggests named non-attendees and flags speakerless first-person commitments accurately.</summary>
    [Fact]
    public async Task ExtractorFlagsSpeakerUnknownOnlyForSpeakerlessFirstPersonCommitment()
    {
        var extractor = new StubLlmExtractor();

        var result = await extractor.ExtractAsync(
            "Priya, can you check the cache by Friday?\nAlex will write the rollout checks by Friday.\nJordan, can you review the report by Friday?\nI can ask the platform team about the alert thresholds.",
            new DateTime(2026, 11, 17), ["Priya", "Alex"]);

        result.Items.Should().HaveCount(4);
        result.Items[0].OwnerName.Should().Be("Priya");
        result.Items[0].NeedsInput.Should().NotContain("speaker unknown");
        result.Items[1].OwnerName.Should().Be("Alex");
        result.Items[1].NeedsInput.Should().NotContain("speaker unknown");
        result.Items[2].OwnerName.Should().BeNull();
        result.Items[2].SuggestedOwnerName.Should().Be("Jordan");
        result.Items[2].NeedsInput.Should().Contain("owner").And.NotContain("speaker unknown");
        result.Items[3].OwnerName.Should().BeNull();
        result.Items[3].SuggestedOwnerName.Should().BeNull();
        result.Items[3].Confidence.Should().BeLessThan(0.5);
        result.Items[3].NeedsInput.Should().Contain("speaker unknown");
    }
}
