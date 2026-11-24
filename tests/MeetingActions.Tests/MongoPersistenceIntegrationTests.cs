using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MeetingActions.Core.Domain;
using MeetingActions.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace MeetingActions.Tests;

/// <summary>Runs MongoDB repository and HTTP integration tests against one shared container.</summary>
[Collection("MongoDB integration")]
public sealed class MongoPersistenceIntegrationTests(MongoDbFixture fixture) : IClassFixture<MongoDbFixture>
{
    /// <summary>Verifies repository query methods and all required collection indexes.</summary>
    [MongoFact]
    public async Task RepositoriesQueryDocumentsAndCreateIndexes()
    {
        await using var testDatabase = await fixture.CreateDatabaseAsync();
        var context = testDatabase.Context;
        var actionItems = new MongoActionItemRepository(context);
        var meetings = new MongoMeetingRepository(context, actionItems);
        var users = new MongoUserRepository(context);
        var followUps = new MongoFollowUpRepository(context);
        var auditEvents = new MongoAuditEventRepository(context);
        var owner = new User { Name = "Priya", Email = "priya@example.invalid" };
        await users.AddManyAsync([owner]);

        var priorMeeting = new Meeting
        {
            Title = "Release Readiness",
            MeetingDate = DateTime.UtcNow.Date.AddDays(-2),
            Attendees = ["Priya"]
        };
        var activeItem = new ActionItem
        {
            MeetingId = priorMeeting.Id,
            Title = "Review release checklist",
            OwnerId = owner.Id,
            OwnerName = owner.Name,
            DueDate = DateTime.UtcNow.Date.AddDays(-1),
            Status = ActionStatus.Overdue,
            SlipCount = 1
        };
        priorMeeting.Items = [activeItem];
        await meetings.AddMeetingAsync(priorMeeting);
        var currentMeeting = new Meeting
        {
            Title = "Release Readiness",
            MeetingDate = DateTime.UtcNow.Date.AddDays(-1),
            Attendees = ["Priya"]
        };
        await meetings.AddMeetingAsync(currentMeeting);

        (await meetings.GetMeetingsAsync()).Should().HaveCount(2);
        (await meetings.GetMeetingAsync(priorMeeting.Id))!.Items.Should().ContainSingle();
        (await meetings.GetNextAgendaItemsAsync(currentMeeting.Id)).Should().ContainSingle()
            .Which.Id.Should().Be(activeItem.Id);
        (await actionItems.GetItemsAsync(priorMeeting.Id)).Should().ContainSingle();
        (await actionItems.GetByStatusAsync(ActionStatus.Overdue)).Should().ContainSingle();
        (await actionItems.GetByOwnerIdAsync(owner.Id)).Should().ContainSingle();
        (await actionItems.GetDueOnOrBeforeAsync(
            DateTime.UtcNow.Date, [ActionStatus.Overdue])).Should().ContainSingle();
        (await actionItems.GetOpenForDashboardAsync()).Should().ContainSingle();
        (await users.GetByNameAsync("pRiYa"))!.Id.Should().Be(owner.Id);

        var sentAt = DateTime.UtcNow;
        var followUp = new FollowUp
        {
            ActionItemId = activeItem.Id,
            SentAt = sentAt,
            Channel = "reminder"
        };
        await followUps.AddAsync(followUp);
        (await followUps.ExistsOnDateAsync(activeItem.Id, "reminder", sentAt)).Should().BeTrue();
        (await followUps.GetByActionItemAsync(activeItem.Id)).Should().ContainSingle();
        var auditEvent = new AuditEvent
        {
            ActionItemId = activeItem.Id,
            EventType = "Reminder sent",
            Actor = "Follow-up service",
            CreatedAt = sentAt
        };
        await auditEvents.AddAsync(auditEvent);
        (await auditEvents.GetByActionItemAsync(activeItem.Id)).Should().ContainSingle();

        var actionIndexNames = await GetIndexNamesAsync(testDatabase.Database, "actionItems");
        actionIndexNames.Should().Contain(
            "ix_actionItems_meetingId",
            "ix_actionItems_status_dueDate",
            "ix_actionItems_ownerId_status");
        (await GetIndexNamesAsync(testDatabase.Database, "meetings"))
            .Should().Contain("ix_meetings_seriesId_meetingDate");
        (await GetIndexNamesAsync(testDatabase.Database, "followUps"))
            .Should().Contain("ix_followUps_actionItemId_sentAt");
        (await GetIndexNamesAsync(testDatabase.Database, "auditEvents"))
            .Should().Contain("ix_auditEvents_actionItemId_createdAt");
    }

    /// <summary>Rejects stale repository snapshots and returns a conflict to one concurrent approver.</summary>
    [MongoFact]
    public async Task OptimisticConcurrencyRejectsStaleUpdatesAndReturnsHttpConflict()
    {
        await using var testDatabase = await fixture.CreateDatabaseAsync();
        using var factory = CreateMongoApi(testDatabase);
        using var client = factory.CreateClient();
        var created = await CreateMeetingAsync(client, "Concurrency release");
        var itemId = created.Items[0].GetProperty("id").GetGuid();
        var repository = new MongoActionItemRepository(testDatabase.Context);
        var staleSnapshot = (await repository.GetItemAsync(itemId))!;
        var concurrentSnapshot = (await repository.GetItemAsync(itemId))!;
        concurrentSnapshot.Title += " updated";
        (await repository.TryUpdateAsync(concurrentSnapshot, concurrentSnapshot.Version)).Should().BeTrue();
        staleSnapshot.Title += " stale";
        (await repository.TryUpdateAsync(staleSnapshot, staleSnapshot.Version)).Should().BeFalse();

        var firstApproval = client.PostAsync($"/api/items/{itemId}/approve", null);
        var secondApproval = client.PostAsync($"/api/items/{itemId}/approve", null);
        var responses = await Task.WhenAll(firstApproval, secondApproval);
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
    }

    /// <summary>Creates, approves, and tickets an extracted meeting action over the real Mongo-backed API.</summary>
    [MongoFact]
    public async Task MeetingCanBeCreatedApprovedAndTicketedOverHttp()
    {
        await using var testDatabase = await fixture.CreateDatabaseAsync();
        using var factory = CreateMongoApi(testDatabase);
        using var client = factory.CreateClient();

        var created = await CreateMeetingAsync(client, "Mongo-backed release");
        var itemId = created.Items[0].GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/items/{itemId}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var ticketResponse = await client.PostAsync($"/api/meetings/{created.Id}/create-tickets", null);

        ticketResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var result = JsonDocument.Parse(await ticketResponse.Content.ReadAsStringAsync());
        var ticket = result.RootElement.GetProperty("createdItems")[0];
        ticket.GetProperty("status").GetString().Should().Be("Created");
        ticket.GetProperty("externalTicketId").GetString().Should().StartWith("ADO-");
    }

    /// <summary>Resets MongoDB and verifies the API seeds the five expected demo tickets and statuses.</summary>
    [MongoFact]
    public async Task ResetDropsAndSeedsMongoCollections()
    {
        await using var testDatabase = await fixture.CreateDatabaseAsync();
        using var factory = CreateMongoApi(testDatabase);
        using var client = factory.CreateClient();

        var resetResponse = await client.PostAsync("/api/dev/reset", null);

        resetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await testDatabase.Database.GetCollection<ActionItem>("actionItems")
            .Find(Builders<ActionItem>.Filter.Empty).ToListAsync();
        items.Should().HaveCount(5);
        items.Select(item => item.Status)
            .Should().BeEquivalentTo(new[]
            {
                ActionStatus.Created,
                ActionStatus.InProgress,
                ActionStatus.Overdue,
                ActionStatus.Overdue,
                ActionStatus.Done
            });
        items.Select(item => item.ExternalTicketId)
            .All(ticketId => !string.IsNullOrEmpty(ticketId)
                && ticketId.StartsWith("ADO-DEMO-", StringComparison.Ordinal))
            .Should().BeTrue();
        var userNames = await testDatabase.Database.GetCollection<User>("users").Find(Builders<User>.Filter.Empty)
            .Project(user => user.Name).ToListAsync();
        userNames.Should().BeEquivalentTo("Priya", "Alex", "Jordan");
    }

    /// <summary>Lists all index names for one database collection.</summary>
    private static async Task<string[]> GetIndexNamesAsync(IMongoDatabase database, string collectionName)
    {
        using var indexes = await database.GetCollection<BsonDocument>(collectionName).Indexes.ListAsync();
        return (await indexes.ToListAsync()).Select(index => index["name"].AsString).ToArray();
    }

    /// <summary>Creates a meeting with one valid extracted action and returns its response body.</summary>
    private static async Task<(Guid Id, JsonElement[] Items)> CreateMeetingAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync("/api/meetings", new
        {
            title,
            date = DateTime.UtcNow.Date,
            attendees = new[] { "Priya" },
            transcript = "Priya, can you review the release checklist by 2099-12-01?"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (
            document.RootElement.GetProperty("id").GetGuid(),
            document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray());
    }

    /// <summary>Creates an API host configured to use the fixture's isolated MongoDB database.</summary>
    private static WebApplicationFactory<Program> CreateMongoApi(MongoTestDatabase testDatabase) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["MongoDB:ConnectionString"] = testDatabase.ConnectionString,
                    ["MongoDB:DatabaseName"] = testDatabase.DatabaseName,
                    ["Llm:Provider"] = "Stub",
                    ["Llm:FallbackToStub"] = "false"
                }));
            builder.ConfigureTestServices(services =>
            {
                // Follow-up processing is invoked directly in tests, so avoid noisy background-job logs.
                services.AddLogging(logging => logging.ClearProviders());
                services.PostConfigure<MeetingActions.Infrastructure.Llm.LlmOptions>(
                    options => options.Provider = "Stub");
                services.RemoveAll<MeetingActions.Core.Abstractions.ILlmExtractor>();
                services.AddSingleton<MeetingActions.Core.Abstractions.ILlmExtractor,
                    MeetingActions.Infrastructure.Integrations.StubLlmExtractor>();
                services.RemoveAll<MeetingActions.Core.Abstractions.ILlmHealth>();
                services.AddSingleton<MeetingActions.Core.Abstractions.ILlmHealth,
                    MeetingActions.Infrastructure.Llm.StubLlmHealth>();
            });
        });
}

/// <summary>Resolves and owns a safe, unique MongoDB database for one integration-test run.</summary>
public sealed class MongoDbFixture : IAsyncLifetime
{
    private const string TestDatabasePrefix = "meeting_actions_test_";
    private MongoDbContainer? _container;
    private MongoClient? _client;
    private MongoContext? _context;
    private string? _connectionString;
    private string? _databaseName;

    /// <summary>Resolves the configured local server or starts one Testcontainers MongoDB instance.</summary>
    public async Task InitializeAsync()
    {
        MongoSerialization.Register();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            _connectionString = Environment.GetEnvironmentVariable("MONGO_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                var skipReason = MongoFactAttribute.GetSkipReason();
                if (skipReason is not null)
                {
                    throw new InvalidOperationException(skipReason);
                }

                _container = new MongoDbBuilder("mongo:7").Build();
                await _container.StartAsync(timeout.Token);
                _connectionString = _container.GetConnectionString();
            }

            ValidateConnectionStringDatabase(_connectionString);
            _databaseName = $"{TestDatabasePrefix}{Guid.NewGuid():N}";
            if (!_databaseName.StartsWith(TestDatabasePrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to use MongoDB database '{_databaseName}'; test database names must start with '{TestDatabasePrefix}'.");
            }

            var clientSettings = MongoClientSettings.FromConnectionString(_connectionString);
            // Mongo integration tests should fail quickly rather than spending the driver's long default waiting for a server.
            clientSettings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
            clientSettings.ConnectTimeout = TimeSpan.FromSeconds(3);
            _client = new MongoClient(clientSettings);
            _context = new MongoContext(_client, Options.Create(new MongoOptions
            {
                ConnectionString = _connectionString,
                DatabaseName = _databaseName
            }));
            await _context.PingAsync(timeout.Token);
            await _context.EnsureIndexesAsync(timeout.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            await DisposeAfterInitializationFailureAsync();
            throw new TimeoutException("MongoDB integration fixture initialization exceeded 60 seconds.", exception);
        }
        catch (MongoException exception)
        {
            await DisposeAfterInitializationFailureAsync();
            throw new InvalidOperationException(
                "MongoDB integration fixture could not connect within 3 seconds. Check MONGO_TEST_CONNECTION and the local server.",
                exception);
        }
    }

    /// <summary>Drops the throwaway database and always disposes its optional container.</summary>
    public async Task DisposeAsync()
    {
        try
        {
            if (_client is not null && _databaseName is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _client.DropDatabaseAsync(_databaseName, timeout.Token);
            }
        }
        finally
        {
            _client?.Dispose();
            if (_container is not null)
            {
                await _container.DisposeAsync();
            }
        }
    }

    /// <summary>Returns the shared run database after clearing prior-test collections and rebuilding indexes.</summary>
    public async Task<MongoTestDatabase> CreateDatabaseAsync()
    {
        if (_client is null || _context is null || _databaseName is null || _connectionString is null)
        {
            throw new InvalidOperationException("The MongoDB test fixture has not been initialized.");
        }

        var testDatabase = new MongoTestDatabase(_client, _context, _databaseName, _connectionString);
        await testDatabase.ClearCollectionsAsync();
        await _context.EnsureIndexesAsync();
        return testDatabase;
    }

    /// <summary>Cleans up partially initialized fixture resources after a timeout or server failure.</summary>
    private async Task DisposeAfterInitializationFailureAsync()
    {
        try
        {
            await DisposeAsync();
        }
        catch (MongoException)
        {
            // A disconnected server cannot drop the unique database; still release its client and container.
            _client?.Dispose();
            if (_container is not null)
            {
                await _container.DisposeAsync();
            }
        }
    }

    /// <summary>Validates any database path before creating a Mongo client or performing database operations.</summary>
    internal static void ValidateConnectionStringDatabase(string connectionString)
    {
        var configuredDatabase = MongoUrl.Create(connectionString).DatabaseName;
        if (!string.IsNullOrWhiteSpace(configuredDatabase)
            && !configuredDatabase.StartsWith(TestDatabasePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to run MongoDB integration tests against '{configuredDatabase}'. "
                + $"The connection-string database must start with '{TestDatabasePrefix}'. "
                + "Use a server-only connection string to let the fixture create a unique test database.");
        }
    }
}

/// <summary>Owns one disposable database and its repositories for a MongoDB integration test.</summary>
public sealed class MongoTestDatabase(
    MongoClient client,
    MongoContext context,
    string databaseName,
    string connectionString) : IAsyncDisposable
{
    /// <summary>Gets the database context used by repositories.</summary>
    public MongoContext Context { get; } = context;

    /// <summary>Gets the isolated database name.</summary>
    public string DatabaseName { get; } = databaseName;

    /// <summary>Gets the connection string shared with the API under test.</summary>
    public string ConnectionString { get; } = connectionString;

    /// <summary>Gets the database handle used for index and collection assertions.</summary>
    public IMongoDatabase Database { get; } = client.GetDatabase(databaseName);

    /// <summary>Removes test collections after each test without dropping the run-scoped database.</summary>
    public async ValueTask DisposeAsync() => await ClearCollectionsAsync();

    /// <summary>Drops every collection so the next integration test starts with clean data.</summary>
    internal async Task ClearCollectionsAsync()
    {
        var collectionNames = await Database.ListCollectionNames().ToListAsync();
        foreach (var collectionName in collectionNames)
        {
            await Database.DropCollectionAsync(collectionName);
        }
    }
}

/// <summary>Runs MongoDB integration tests when a local connection or Docker-backed server is available.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class MongoFactAttribute : FactAttribute
{
    /// <summary>Skips without a MongoDB source and bounds every Mongo integration test to one minute.</summary>
    public MongoFactAttribute()
    {
        Timeout = 60_000;
        Skip = GetSkipReason();
    }

    /// <summary>Gets the skip message for an integration suite without a local server or Docker.</summary>
    internal static string? GetSkipReason()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGO_TEST_CONNECTION")))
        {
            return null;
        }

        return CheckDocker();
    }

    /// <summary>Checks Docker availability once during test discovery.</summary>
    private static string? CheckDocker()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "info --format {{.ServerVersion}}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                return "Skipped: Docker is unavailable; MongoDB integration tests require Docker.";
            }

            if (!process.WaitForExit(3000))
            {
                process.Kill();
                return "Skipped: Docker did not respond; MongoDB integration tests require Docker.";
            }

            return process.ExitCode == 0
                ? null
                : "Skipped: Docker daemon is unavailable; MongoDB integration tests require Docker.";
        }
        catch (Win32Exception)
        {
            return "Skipped: Docker is not installed; MongoDB integration tests require Docker.";
        }
        catch (InvalidOperationException)
        {
            return "Skipped: Docker could not be started; MongoDB integration tests require Docker.";
        }
    }
}

/// <summary>Prevents an unavailable MongoDB server from making the API report healthy.</summary>
public sealed class MongoHealthDegradedTests
{
    /// <summary>Reports degraded health without exposing the unreachable MongoDB connection string.</summary>
    [Fact]
    public async Task HealthReportsDegradedWhenMongoIsUnreachable()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["MongoDB:ConnectionString"] = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100",
                    ["MongoDB:DatabaseName"] = "meeting_actions_test_unreachable",
                    // This test isolates Mongo failure behavior from any locally installed or running model.
                    ["Llm:Provider"] = "Stub",
                    ["Llm:FallbackToStub"] = "false"
                }));
            builder.ConfigureTestServices(services =>
                services.AddLogging(logging => logging.ClearProviders()));
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().Should().Be("Degraded");
        document.RootElement.GetProperty("message").GetString().Should().NotContain("127.0.0.1");

        var unavailableResponse = await client.PostAsJsonAsync("/api/meetings", new
        {
            title = "Unavailable database",
            date = DateTime.UtcNow.Date,
            attendees = new[] { "Priya" },
            transcript = "Priya, can you review the limits by 2099-12-01?"
        });
        unavailableResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        unavailableResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        (await unavailableResponse.Content.ReadAsStringAsync()).Should().NotContain("127.0.0.1");
    }
}

/// <summary>Verifies MongoDB test isolation rules without requiring a database server.</summary>
public sealed class MongoDatabaseSafetyTests
{
    /// <summary>Refuses a configured connection string that names a non-test database.</summary>
    [Fact]
    public void FixtureRejectsConnectionStringsForUnrelatedDatabases()
    {
        var act = () => MongoDbFixture.ValidateConnectionStringDatabase(
            "mongodb://localhost:27017/FlightComplianceDB");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*meeting_actions_test_*");
    }
}
