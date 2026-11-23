using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeetingActions.Tests;

/// <summary>Implements every repository contract with isolated in-memory collections for fast API tests.</summary>
internal sealed class InMemoryRepositoryStore :
    IMeetingRepository,
    IActionItemRepository,
    IUserRepository,
    IFollowUpRepository,
    IAuditEventRepository,
    IDemoDataStore,
    IPersistenceHealthCheck
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Meeting> _meetings = [];
    private readonly Dictionary<Guid, ActionItem> _items = [];
    private readonly Dictionary<Guid, User> _users = [];
    private readonly List<FollowUp> _followUps = [];
    private readonly List<AuditEvent> _auditEvents = [];

    /// <summary>Gets a snapshot of stored action items for assertions.</summary>
    public IReadOnlyList<ActionItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.Values.Select(CloneItem).ToArray();
            }
        }
    }

    /// <summary>Gets a snapshot of stored follow-ups for assertions.</summary>
    public IReadOnlyList<FollowUp> FollowUps
    {
        get
        {
            lock (_gate)
            {
                return _followUps.Select(CloneFollowUp).ToArray();
            }
        }
    }

    /// <summary>Gets a snapshot of stored audit events for assertions.</summary>
    public IReadOnlyList<AuditEvent> AuditEvents
    {
        get
        {
            lock (_gate)
            {
                return _auditEvents.Select(CloneAuditEvent).ToArray();
            }
        }
    }

    /// <summary>Gets all meetings with their separately stored action items.</summary>
    public Task<IReadOnlyList<Meeting>> GetMeetingsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Meeting>>(_meetings.Values
                .OrderByDescending(meeting => meeting.MeetingDate)
                .Select(CloneMeetingWithItems)
                .ToArray());
        }
    }

    /// <summary>Gets one meeting with its separately stored action items.</summary>
    public Task<Meeting?> GetMeetingAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_meetings.TryGetValue(id, out var meeting) ? CloneMeetingWithItems(meeting) : null);
        }
    }

    /// <summary>Persists a meeting and its draft items.</summary>
    public Task AddMeetingAsync(Meeting meeting, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _meetings[meeting.Id] = CloneMeeting(meeting);
            foreach (var item in meeting.Items)
            {
                _items[item.Id] = CloneItem(item);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Persists meeting metadata changes.</summary>
    public Task UpdateMeetingAsync(Meeting meeting, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_meetings.ContainsKey(meeting.Id))
            {
                _meetings[meeting.Id] = CloneMeeting(meeting);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Gets open items from earlier meetings sharing this meeting's title.</summary>
    public Task<IReadOnlyList<ActionItem>> GetNextAgendaItemsAsync(
        Guid meetingId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_meetings.TryGetValue(meetingId, out var current))
            {
                return Task.FromResult<IReadOnlyList<ActionItem>>([]);
            }

            var meetingIds = _meetings.Values
                .Where(meeting => meeting.Id != meetingId
                    && meeting.Title == current.Title
                    && meeting.MeetingDate < current.MeetingDate)
                .Select(meeting => meeting.Id)
                .ToHashSet();
            return Task.FromResult<IReadOnlyList<ActionItem>>(_items.Values
                .Where(item => meetingIds.Contains(item.MeetingId)
                    && item.Status is not ActionStatus.Done and not ActionStatus.Rejected)
                .Select(CloneItem)
                .ToArray());
        }
    }

    /// <summary>Gets one action item by its identifier.</summary>
    public Task<ActionItem?> GetItemAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.TryGetValue(id, out var item) ? CloneItem(item) : null);
        }
    }

    /// <summary>Gets action items for a meeting.</summary>
    public Task<IReadOnlyList<ActionItem>> GetItemsAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ActionItem>>(_items.Values
                .Where(item => item.MeetingId == meetingId).Select(CloneItem).ToArray());
        }
    }

    /// <summary>Gets every action item.</summary>
    public Task<IReadOnlyList<ActionItem>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ActionItem>>(_items.Values.Select(CloneItem).ToArray());
        }
    }

    /// <summary>Gets items in the requested workflow status.</summary>
    public Task<IReadOnlyList<ActionItem>> GetByStatusAsync(
        ActionStatus status,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ActionItem>>(_items.Values
                .Where(item => item.Status == status).Select(CloneItem).ToArray());
        }
    }

    /// <summary>Gets items due on or before a date in the requested statuses.</summary>
    public Task<IReadOnlyList<ActionItem>> GetDueOnOrBeforeAsync(
        DateTime date,
        IReadOnlyCollection<ActionStatus> statuses,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ActionItem>>(_items.Values
                .Where(item => item.DueDate is not null && item.DueDate <= date && statuses.Contains(item.Status))
                .Select(CloneItem).ToArray());
        }
    }

    /// <summary>Gets items assigned to a user.</summary>
    public Task<IReadOnlyList<ActionItem>> GetByOwnerIdAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ActionItem>>(_items.Values
                .Where(item => item.OwnerId == ownerId).Select(CloneItem).ToArray());
        }
    }

    /// <summary>Gets active workflow items used by dashboard owner counts.</summary>
    public Task<IReadOnlyList<ActionItem>> GetOpenForDashboardAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ActionItem>>(_items.Values
                .Where(item => item.Status is ActionStatus.Approved or ActionStatus.Created or ActionStatus.InProgress
                    or ActionStatus.Overdue or ActionStatus.Escalated)
                .Select(CloneItem).ToArray());
        }
    }

    /// <summary>Inserts action items independently of meeting document storage.</summary>
    public Task AddManyAsync(IReadOnlyCollection<ActionItem> items, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var item in items)
            {
                _items[item.Id] = CloneItem(item);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Updates an item only when its version still matches the caller's snapshot.</summary>
    public Task<bool> TryUpdateAsync(
        ActionItem item,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(item.Id, out var existing) || existing.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }

            var updated = CloneItem(item);
            updated.Version = expectedVersion + 1;
            _items[item.Id] = updated;
            item.Version = updated.Version;
            return Task.FromResult(true);
        }
    }

    /// <summary>Gets all users ordered by name.</summary>
    public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<User>>(_users.Values.OrderBy(user => user.Name)
                .Select(CloneUser).ToArray());
        }
    }

    /// <summary>Finds one user by case-insensitive display name.</summary>
    public Task<User?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_users.Values.FirstOrDefault(user =>
                string.Equals(user.Name, name, StringComparison.OrdinalIgnoreCase)) is { } user
                ? CloneUser(user)
                : null);
        }
    }

    /// <summary>Inserts users for the demo environment.</summary>
    public Task AddManyAsync(IReadOnlyCollection<User> users, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var user in users)
            {
                _users[user.Id] = CloneUser(user);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Records one follow-up.</summary>
    public Task AddAsync(FollowUp followUp, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _followUps.Add(CloneFollowUp(followUp));
        }

        return Task.CompletedTask;
    }

    /// <summary>Records multiple follow-ups.</summary>
    public Task AddManyAsync(IReadOnlyCollection<FollowUp> followUps, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _followUps.AddRange(followUps.Select(CloneFollowUp));
        }

        return Task.CompletedTask;
    }

    /// <summary>Checks whether an item already has a follow-up of this channel on a UTC date.</summary>
    public Task<bool> ExistsOnDateAsync(
        Guid actionItemId,
        string channel,
        DateTime utcDate,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_followUps.Any(followUp => followUp.ActionItemId == actionItemId
                && followUp.Channel == channel
                && followUp.SentAt.Date == utcDate.Date));
        }
    }

    /// <summary>Gets all follow-ups for an action item in chronological order.</summary>
    public Task<IReadOnlyList<FollowUp>> GetByActionItemAsync(
        Guid actionItemId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<FollowUp>>(_followUps
                .Where(followUp => followUp.ActionItemId == actionItemId)
                .OrderBy(followUp => followUp.SentAt).Select(CloneFollowUp).ToArray());
        }
    }

    /// <summary>Gets every follow-up for operational verification.</summary>
    Task<IReadOnlyList<FollowUp>> IFollowUpRepository.GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult(FollowUps);

    /// <summary>Records one audit event.</summary>
    public Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _auditEvents.Add(CloneAuditEvent(auditEvent));
        }

        return Task.CompletedTask;
    }

    /// <summary>Records multiple audit events.</summary>
    public Task AddManyAsync(IReadOnlyCollection<AuditEvent> auditEvents, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _auditEvents.AddRange(auditEvents.Select(CloneAuditEvent));
        }

        return Task.CompletedTask;
    }

    /// <summary>Gets an action item's audit events in chronological order.</summary>
    Task<IReadOnlyList<AuditEvent>> IAuditEventRepository.GetByActionItemAsync(
        Guid actionItemId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditEvent>>(AuditEvents
            .Where(auditEvent => auditEvent.ActionItemId == actionItemId)
            .OrderBy(auditEvent => auditEvent.CreatedAt).ToArray());

    /// <summary>Returns whether the fake demo store has no existing demo users or meetings.</summary>
    public Task<bool> IsEmptyAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_users.Count == 0 && _meetings.Count == 0);
        }
    }

    /// <summary>Seeds the initial users, meeting, and extracted items when the store is empty.</summary>
    public async Task SeedAsync(
        IReadOnlyCollection<User> users,
        Meeting meeting,
        CancellationToken cancellationToken = default)
    {
        if (!await IsEmptyAsync(cancellationToken))
        {
            return;
        }

        await AddManyAsync(users, cancellationToken);
        await AddMeetingAsync(meeting, cancellationToken);
    }

    /// <summary>Clears fake persisted data and replaces it with the supplied scenario.</summary>
    public Task ResetAsync(
        IReadOnlyCollection<User> users,
        Meeting meeting,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _meetings.Clear();
            _items.Clear();
            _users.Clear();
            _followUps.Clear();
            _auditEvents.Clear();
            foreach (var user in users)
            {
                _users[user.Id] = CloneUser(user);
            }

            _meetings[meeting.Id] = CloneMeeting(meeting);
            foreach (var item in meeting.Items)
            {
                _items[item.Id] = CloneItem(item);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Reports the in-memory fake as available to health endpoint tests.</summary>
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <summary>Creates a meeting snapshot with its separate action items.</summary>
    private Meeting CloneMeetingWithItems(Meeting meeting)
    {
        var clone = CloneMeeting(meeting);
        clone.Items = _items.Values.Where(item => item.MeetingId == meeting.Id).Select(CloneItem).ToList();
        return clone;
    }

    /// <summary>Copies meeting metadata without copying its separate action-item collection.</summary>
    private static Meeting CloneMeeting(Meeting meeting) =>
        new()
        {
            Id = meeting.Id,
            Title = meeting.Title,
            MeetingDate = meeting.MeetingDate,
            Transcript = meeting.Transcript,
            Attendees = meeting.Attendees.ToList(),
            Decisions = meeting.Decisions?.ToList(),
            Analysis = meeting.Analysis
        };

    /// <summary>Copies an action item to isolate reads from the fake store.</summary>
    private static ActionItem CloneItem(ActionItem item) =>
        new()
        {
            Id = item.Id,
            MeetingId = item.MeetingId,
            Title = item.Title,
            OwnerId = item.OwnerId,
            OwnerName = item.OwnerName,
            SuggestedOwnerName = item.SuggestedOwnerName,
            DueDate = item.DueDate,
            DueDateText = item.DueDateText,
            Status = item.Status,
            Confidence = item.Confidence,
            SourceQuote = item.SourceQuote,
            Timestamp = item.Timestamp,
            NeedsInput = item.NeedsInput.ToList(),
            ExternalTicketId = item.ExternalTicketId,
            SlipCount = item.SlipCount,
            Version = item.Version
        };

    /// <summary>Copies a user record.</summary>
    private static User CloneUser(User user) =>
        new()
        {
            Id = user.Id,
            Name = user.Name,
            Email = user.Email,
            ExternalUserId = user.ExternalUserId
        };

    /// <summary>Copies a follow-up record.</summary>
    private static FollowUp CloneFollowUp(FollowUp followUp) =>
        new()
        {
            Id = followUp.Id,
            ActionItemId = followUp.ActionItemId,
            SentAt = followUp.SentAt,
            Channel = followUp.Channel,
            Response = followUp.Response
        };

    /// <summary>Copies an audit event.</summary>
    private static AuditEvent CloneAuditEvent(AuditEvent auditEvent) =>
        new()
        {
            Id = auditEvent.Id,
            ActionItemId = auditEvent.ActionItemId,
            EventType = auditEvent.EventType,
            Actor = auditEvent.Actor,
            CreatedAt = auditEvent.CreatedAt
        };
}

/// <summary>Creates API test hosts with in-memory persistence and no MongoDB network dependency.</summary>
internal static class TestApiFactory
{
    /// <summary>Builds an API host backed by the supplied fake repositories.</summary>
    public static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Create(
        InMemoryRepositoryStore store,
        string environment = "Development",
        ILlmExtractor? extractor = null,
        ITaskProvider? taskProvider = null,
        string llmProvider = "Stub",
        ILlmHealth? llmHealth = null,
        bool llmFallbackToStub = false)
    {
        return new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["MongoDB:ConnectionString"] = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
                        ["MongoDB:DatabaseName"] = "meeting_actions_test_fakes",
                        ["Llm:Provider"] = llmProvider,
                        ["Llm:FallbackToStub"] = llmFallbackToStub.ToString()
                    }));
                builder.ConfigureTestServices(services =>
                {
                    // Tests invoke follow-up processing directly, so suppress noisy Hangfire host logs.
                    services.AddLogging(logging => logging.ClearProviders());
                    services.PostConfigure<MeetingActions.Infrastructure.Llm.LlmOptions>(
                        options =>
                        {
                            options.Provider = llmProvider;
                            options.FallbackToStub = llmFallbackToStub;
                        });
                    Replace(services, store);
                    services.RemoveAll<MeetingActions.Infrastructure.Persistence.IMongoDatabaseInitializer>();
                    services.AddSingleton<MeetingActions.Infrastructure.Persistence.IMongoDatabaseInitializer,
                        NoOpMongoDatabaseInitializer>();
                    services.RemoveAll<ILlmExtractor>();
                    services.AddSingleton<ILlmExtractor>(
                        extractor ?? new MeetingActions.Infrastructure.Integrations.StubLlmExtractor());

                    services.RemoveAll<ILlmHealth>();
                    services.AddSingleton(llmHealth
                        ?? new MeetingActions.Infrastructure.Llm.StubLlmHealth());

                    if (taskProvider is not null)
                    {
                        services.RemoveAll<ITaskProvider>();
                        services.AddSingleton(taskProvider);
                    }
                });
            });
    }

    /// <summary>Replaces all persistence abstractions with the same isolated fake instance.</summary>
    private static void Replace(IServiceCollection services, InMemoryRepositoryStore store)
    {
        services.RemoveAll<IMeetingRepository>();
        services.RemoveAll<IActionItemRepository>();
        services.RemoveAll<IUserRepository>();
        services.RemoveAll<IFollowUpRepository>();
        services.RemoveAll<IAuditEventRepository>();
        services.RemoveAll<IDemoDataStore>();
        services.RemoveAll<IPersistenceHealthCheck>();
        services.AddSingleton<IMeetingRepository>(store);
        services.AddSingleton<IActionItemRepository>(store);
        services.AddSingleton<IUserRepository>(store);
        services.AddSingleton<IFollowUpRepository>(store);
        services.AddSingleton<IAuditEventRepository>(store);
        services.AddSingleton<IDemoDataStore>(store);
        services.AddSingleton<IPersistenceHealthCheck>(store);
    }

    /// <summary>Skips Mongo index creation in API tests that replace all repositories with fakes.</summary>
    private sealed class NoOpMongoDatabaseInitializer : MeetingActions.Infrastructure.Persistence.IMongoDatabaseInitializer
    {
        /// <summary>Completes without connecting because test repositories do not use MongoDB.</summary>
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
