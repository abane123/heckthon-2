using FluentAssertions;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MeetingActions.Tests;

/// <summary>Checks reminders, overdue transitions, and repeated-slip escalation without a database.</summary>
public sealed class FollowUpServiceTests
{
    /// <summary>Sends the day-before reminder and records it for the action item.</summary>
    [Fact]
    public async Task RunAsyncSendsReminderOneDayBeforeDueDate()
    {
        var setup = CreateService(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 2, 17, 0, 0, DateTimeKind.Utc), ActionStatus.Created);

        var remindersSent = await setup.Service.RunAsync();

        remindersSent.Should().Be(1);
        setup.Store.FollowUps.Single().Channel.Should().Be("reminder");
        setup.Notifier.Verify(notifier => notifier.SendAsync("Priya", It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Marks an active ticket overdue on its due date.</summary>
    [Fact]
    public async Task RunAsyncMarksItemOverdueOnDueDate()
    {
        var setup = CreateService(new DateTime(2026, 2, 2, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 2, 8, 0, 0, DateTimeKind.Utc), ActionStatus.Created);

        await setup.Service.RunAsync();

        var item = setup.Store.Items.Single();
        item.Status.Should().Be(ActionStatus.Overdue);
        item.SlipCount.Should().Be(1);
        setup.Store.FollowUps.Single(followUp => followUp.Channel == "overdue").Should().NotBeNull();
    }

    /// <summary>Escalates the action after it accumulates two daily slips.</summary>
    [Fact]
    public async Task RunAsyncEscalatesAfterTwoSlips()
    {
        var setup = CreateService(new DateTime(2026, 2, 2, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 2, 8, 0, 0, DateTimeKind.Utc), ActionStatus.Created);

        await setup.Service.RunAsync();
        setup.Clock.AdvanceDays(1);
        await setup.Service.RunAsync();

        var item = setup.Store.Items.Single();
        item.Status.Should().Be(ActionStatus.Escalated);
        item.SlipCount.Should().Be(2);
        setup.Store.FollowUps.Count(followUp => followUp.Channel == "overdue").Should().Be(2);
        setup.Store.FollowUps.Count(followUp => followUp.Channel == "escalation").Should().Be(1);
    }

    /// <summary>Creates a follow-up service with hand-written repository fakes and deterministic time.</summary>
    private static FollowUpTestSetup CreateService(DateTime now, DateTime dueDate, ActionStatus status)
    {
        var store = new InMemoryFollowUpStore();
        store.Items.Add(new ActionItem
        {
            MeetingId = Guid.NewGuid(),
            Title = "Review rate limits",
            OwnerName = "Priya",
            Status = status,
            DueDate = dueDate,
            ExternalTicketId = "MOCK-0001"
        });

        var taskProvider = new Mock<ITaskProvider>();
        taskProvider.Setup(provider => provider.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Active");
        var notifier = new Mock<INotifier>();
        var clock = new VirtualClock(now);
        var service = new FollowUpService(store, store, store, taskProvider.Object, notifier.Object, clock,
            new ActionItemStateMachine(), NullLogger<FollowUpService>.Instance);
        return new FollowUpTestSetup(store, service, clock, notifier);
    }

    /// <summary>Contains in-memory action, follow-up, and audit records for fast service tests.</summary>
    private sealed class InMemoryFollowUpStore :
        IActionItemRepository,
        IFollowUpRepository,
        IAuditEventRepository
    {
        /// <summary>Gets the test action items.</summary>
        public List<ActionItem> Items { get; } = [];

        /// <summary>Gets the persisted follow-up records.</summary>
        public List<FollowUp> FollowUps { get; } = [];

        /// <summary>Gets the persisted audit records.</summary>
        public List<AuditEvent> AuditEvents { get; } = [];

        /// <summary>Gets an action item by its identifier.</summary>
        public Task<ActionItem?> GetItemAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.FirstOrDefault(item => item.Id == id));

        /// <summary>Gets action items for a meeting.</summary>
        public Task<IReadOnlyList<ActionItem>> GetItemsAsync(Guid meetingId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActionItem>>(Items.Where(item => item.MeetingId == meetingId).ToArray());

        /// <summary>Gets every action item.</summary>
        public Task<IReadOnlyList<ActionItem>> GetAllItemsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActionItem>>(Items.ToArray());

        /// <summary>Gets items in the requested workflow status.</summary>
        public Task<IReadOnlyList<ActionItem>> GetByStatusAsync(
            ActionStatus status,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActionItem>>(Items.Where(item => item.Status == status).ToArray());

        /// <summary>Gets items due on or before a date in the requested statuses.</summary>
        public Task<IReadOnlyList<ActionItem>> GetDueOnOrBeforeAsync(
            DateTime date,
            IReadOnlyCollection<ActionStatus> statuses,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActionItem>>(Items
                .Where(item => item.DueDate is not null && item.DueDate <= date && statuses.Contains(item.Status))
                .ToArray());

        /// <summary>Gets items assigned to a user.</summary>
        public Task<IReadOnlyList<ActionItem>> GetByOwnerIdAsync(
            Guid ownerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActionItem>>(Items.Where(item => item.OwnerId == ownerId).ToArray());

        /// <summary>Gets active workflow items used by dashboard owner counts.</summary>
        public Task<IReadOnlyList<ActionItem>> GetOpenForDashboardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActionItem>>(Items.Where(item => item.Status is
                ActionStatus.Approved or ActionStatus.Created or ActionStatus.InProgress
                or ActionStatus.Overdue or ActionStatus.Escalated).ToArray());

        /// <summary>Inserts action items independently of meeting document storage.</summary>
        public Task AddManyAsync(IReadOnlyCollection<ActionItem> items, CancellationToken cancellationToken = default)
        {
            Items.AddRange(items);
            return Task.CompletedTask;
        }

        /// <summary>Updates an item only when its version still matches the caller's snapshot.</summary>
        public Task<bool> TryUpdateAsync(
            ActionItem item,
            int expectedVersion,
            CancellationToken cancellationToken = default)
        {
            if (item.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }

            item.Version++;
            return Task.FromResult(true);
        }

        /// <summary>Records one follow-up.</summary>
        public Task AddAsync(FollowUp followUp, CancellationToken cancellationToken = default)
        {
            FollowUps.Add(followUp);
            return Task.CompletedTask;
        }

        /// <summary>Records multiple follow-ups.</summary>
        public Task AddManyAsync(IReadOnlyCollection<FollowUp> followUps, CancellationToken cancellationToken = default)
        {
            FollowUps.AddRange(followUps);
            return Task.CompletedTask;
        }

        /// <summary>Checks whether an item already has a follow-up of this channel on a UTC date.</summary>
        public Task<bool> ExistsOnDateAsync(
            Guid actionItemId,
            string channel,
            DateTime utcDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(FollowUps.Any(followUp =>
                followUp.ActionItemId == actionItemId
                && followUp.Channel == channel
                && followUp.SentAt.Date == utcDate.Date));

        /// <summary>Gets all follow-ups for an action item in chronological order.</summary>
        public Task<IReadOnlyList<FollowUp>> GetByActionItemAsync(
            Guid actionItemId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FollowUp>>(FollowUps
                .Where(followUp => followUp.ActionItemId == actionItemId)
                .OrderBy(followUp => followUp.SentAt).ToArray());

        /// <summary>Gets every follow-up for operational verification.</summary>
        public Task<IReadOnlyList<FollowUp>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FollowUp>>(FollowUps.ToArray());

        /// <summary>Records one audit event.</summary>
        public Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            AuditEvents.Add(auditEvent);
            return Task.CompletedTask;
        }

        /// <summary>Records multiple audit events.</summary>
        public Task AddManyAsync(IReadOnlyCollection<AuditEvent> auditEvents, CancellationToken cancellationToken = default)
        {
            AuditEvents.AddRange(auditEvents);
            return Task.CompletedTask;
        }

        /// <summary>Gets an action item's audit events in chronological order.</summary>
        Task<IReadOnlyList<AuditEvent>> IAuditEventRepository.GetByActionItemAsync(
            Guid actionItemId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuditEvent>>(AuditEvents
                .Where(auditEvent => auditEvent.ActionItemId == actionItemId)
                .OrderBy(auditEvent => auditEvent.CreatedAt).ToArray());
    }

    /// <summary>Groups the fakes and collaborators used by each follow-up test.</summary>
    private sealed record FollowUpTestSetup(
        InMemoryFollowUpStore Store,
        FollowUpService Service,
        VirtualClock Clock,
        Mock<INotifier> Notifier);
}
