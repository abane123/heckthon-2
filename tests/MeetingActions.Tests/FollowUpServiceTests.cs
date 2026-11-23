using FluentAssertions;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Infrastructure.Persistence;
using MeetingActions.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MeetingActions.Tests;

/// <summary>Checks reminders, overdue transitions, and repeated-slip escalation.</summary>
public sealed class FollowUpServiceTests
{
    /// <summary>Sends the day-before reminder and records it for the action item.</summary>
    [Fact]
    public async Task RunAsyncSendsReminderOneDayBeforeDueDate()
    {
        var setup = await CreateService(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 2, 17, 0, 0, DateTimeKind.Utc), ActionStatus.Created);
        await using var dbContext = setup.DbContext;

        var remindersSent = await setup.Service.RunAsync();

        remindersSent.Should().Be(1);
        (await dbContext.FollowUps.SingleAsync()).Channel.Should().Be("reminder");
        setup.Notifier.Verify(notifier => notifier.SendAsync("Priya", It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Marks an active ticket overdue on its due date.</summary>
    [Fact]
    public async Task RunAsyncMarksItemOverdueOnDueDate()
    {
        var setup = await CreateService(new DateTime(2026, 2, 2, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 2, 8, 0, 0, DateTimeKind.Utc), ActionStatus.Created);
        await using var dbContext = setup.DbContext;

        await setup.Service.RunAsync();

        var item = await dbContext.ActionItems.SingleAsync();
        item.Status.Should().Be(ActionStatus.Overdue);
        item.SlipCount.Should().Be(1);
        (await dbContext.FollowUps.SingleAsync(followUp => followUp.Channel == "overdue")).Should().NotBeNull();
    }

    /// <summary>Escalates the action after it accumulates two daily slips.</summary>
    [Fact]
    public async Task RunAsyncEscalatesAfterTwoSlips()
    {
        var setup = await CreateService(new DateTime(2026, 2, 2, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 2, 8, 0, 0, DateTimeKind.Utc), ActionStatus.Created);
        await using var dbContext = setup.DbContext;

        await setup.Service.RunAsync();
        setup.Clock.AdvanceDays(1);
        await setup.Service.RunAsync();

        var item = await dbContext.ActionItems.SingleAsync();
        item.Status.Should().Be(ActionStatus.Escalated);
        item.SlipCount.Should().Be(2);
        (await dbContext.FollowUps.CountAsync(followUp => followUp.Channel == "overdue")).Should().Be(2);
        (await dbContext.FollowUps.CountAsync(followUp => followUp.Channel == "escalation")).Should().Be(1);
    }

    private static async Task<FollowUpTestSetup> CreateService(DateTime now, DateTime dueDate, ActionStatus status)
    {
        var options = new DbContextOptionsBuilder<MeetingActionsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new MeetingActionsDbContext(options);
        var meeting = new Meeting { Title = "Follow-up test", MeetingDate = now.AddDays(-1) };
        var item = new ActionItem
        {
            MeetingId = meeting.Id,
            Meeting = meeting,
            Title = "Review rate limits",
            OwnerName = "Priya",
            Status = status,
            DueDate = dueDate,
            ExternalTicketId = "MOCK-0001"
        };
        meeting.Items.Add(item);
        await dbContext.Meetings.AddAsync(meeting);
        await dbContext.SaveChangesAsync();

        var taskProvider = new Mock<ITaskProvider>();
        taskProvider.Setup(provider => provider.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Active");
        var notifier = new Mock<INotifier>();
        var clock = new VirtualClock(now);
        var service = new FollowUpService(dbContext, taskProvider.Object, notifier.Object, clock,
            new ActionItemStateMachine(), NullLogger<FollowUpService>.Instance);
        return new FollowUpTestSetup(dbContext, service, clock, notifier);
    }

    private sealed record FollowUpTestSetup(
        MeetingActionsDbContext DbContext,
        FollowUpService Service,
        VirtualClock Clock,
        Mock<INotifier> Notifier);
}
