using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MeetingActions.Tests;

/// <summary>Exercises meeting creation, owner resolution, review, and ticket rules over HTTP.</summary>
public sealed class MeetingApiIntegrationTests
{
    /// <summary>Creates a meeting, approves its extracted item, and creates a ticket.</summary>
    [Fact]
    public async Task MeetingCanBeCreatedApprovedAndConvertedToTicket()
    {
        var store = new InMemoryRepositoryStore();
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        var createResponse = await client.PostAsJsonAsync("/api/meetings", new
        {
            title = "Integration release",
            date = "2026-11-17T00:00:00Z",
            attendees = new[] { "Priya" },
            transcript = "Priya, can you review the rate limits by 2099-12-01?"
        });
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var meetingDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var itemId = meetingDocument.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();
        meetingDocument.RootElement.GetProperty("items")[0].GetProperty("status").GetString().Should().Be("Draft");

        var approveResponse = await client.PostAsync($"/api/items/{itemId}/approve", null);
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var approvedDocument = JsonDocument.Parse(await approveResponse.Content.ReadAsStringAsync());
        approvedDocument.RootElement.GetProperty("status").GetString().Should().Be("Approved");

        var undoResponse = await client.PostAsync($"/api/items/{itemId}/unapprove", null);
        undoResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var undoneDocument = JsonDocument.Parse(await undoResponse.Content.ReadAsStringAsync());
        undoneDocument.RootElement.GetProperty("status").GetString().Should().Be("Draft");
        (await client.PostAsync($"/api/items/{itemId}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var meetingId = meetingDocument.RootElement.GetProperty("id").GetGuid();
        var ticketResponse = await client.PostAsync($"/api/meetings/{meetingId}/create-tickets", null);
        ticketResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var ticketDocument = JsonDocument.Parse(await ticketResponse.Content.ReadAsStringAsync());
        ticketDocument.RootElement.GetProperty("createdItems")[0].GetProperty("externalTicketId").GetString().Should().StartWith("ADO-");
        ticketDocument.RootElement.GetProperty("createdItems")[0].GetProperty("status").GetString().Should().Be("Created");
    }

    /// <summary>Restores a rejected action through the API and verifies its draft status.</summary>
    [Fact]
    public async Task RejectedActionCanBeRestoredForReview()
    {
        using var factory = CreateFactory(new InMemoryRepositoryStore());
        using var client = factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/meetings", new
        {
            title = "Restore action",
            date = "2026-11-17T00:00:00Z",
            attendees = new[] { "Priya" },
            transcript = "Priya, can you review the rate limits by 2099-12-01?"
        });
        using var meetingDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var itemId = meetingDocument.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();

        (await client.PostAsync($"/api/items/{itemId}/reject", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var restoreResponse = await client.PostAsync($"/api/items/{itemId}/restore", null);

        restoreResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var restoredDocument = JsonDocument.Parse(await restoreResponse.Content.ReadAsStringAsync());
        restoredDocument.RootElement.GetProperty("status").GetString().Should().Be("Draft");
    }

    /// <summary>Adds a newly identified attendee and resolves only matching draft action owners.</summary>
    [Fact]
    public async Task AddingAttendeeResolvesDraftOwnerAndLeavesApprovedItemsUnchanged()
    {
        using var factory = CreateFactory(new InMemoryRepositoryStore());
        using var client = factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/meetings", new
        {
            title = "Attendee resolution",
            date = "2026-11-17T00:00:00Z",
            attendees = new[] { "Priya" },
            transcript = "Jordan, can you review the report by 2099-12-01?\nPriya, can you check the rollout by 2099-12-02?\nI can verify the docs by 2099-12-03."
        });
        using var meetingDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var meetingId = meetingDocument.RootElement.GetProperty("id").GetGuid();
        var items = meetingDocument.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var jordanItemId = items[0].GetProperty("id").GetGuid();
        items[0].GetProperty("suggestedOwnerName").GetString().Should().Be("Jordan");
        items.Should().HaveCount(3);
        var approvedItemId = items[1].GetProperty("id").GetGuid();
        (await client.PostAsync($"/api/items/{approvedItemId}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var manuallyAssignedItemId = items[2].GetProperty("id").GetGuid();
        var editResponse = await client.PutAsJsonAsync($"/api/items/{manuallyAssignedItemId}", new
        {
            title = "Verify documentation",
            ownerName = "Alex",
            dueDate = "2099-12-03T00:00:00Z"
        });
        editResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var addResponse = await client.PostAsJsonAsync($"/api/meetings/{meetingId}/attendees", new { name = " Jordan " });

        addResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var updatedMeeting = JsonDocument.Parse(await addResponse.Content.ReadAsStringAsync());
        updatedMeeting.RootElement.GetProperty("attendees").EnumerateArray()
            .Select(attendee => attendee.GetString()).Should().Contain("Jordan");
        var resolvedJordan = updatedMeeting.RootElement.GetProperty("items").EnumerateArray()
            .Single(action => action.GetProperty("id").GetGuid() == jordanItemId);
        resolvedJordan.GetProperty("ownerName").GetString().Should().Be("Jordan");
        resolvedJordan.GetProperty("suggestedOwnerName").ValueKind.Should().Be(JsonValueKind.Null);
        resolvedJordan.GetProperty("needsInput").EnumerateArray()
            .Select(value => value.GetString()).Should().NotContain("owner");
        var unchangedApproved = updatedMeeting.RootElement.GetProperty("items").EnumerateArray()
            .Single(action => action.GetProperty("id").GetGuid() == approvedItemId);
        unchangedApproved.GetProperty("status").GetString().Should().Be("Approved");
        unchangedApproved.GetProperty("ownerName").GetString().Should().Be("Priya");
        var unchangedManualOwner = updatedMeeting.RootElement.GetProperty("items").EnumerateArray()
            .Single(action => action.GetProperty("id").GetGuid() == manuallyAssignedItemId);
        unchangedManualOwner.GetProperty("ownerName").GetString().Should().Be("Alex");
        unchangedManualOwner.GetProperty("needsInput").GetArrayLength().Should().Be(0);
    }

    /// <summary>Persists ticket successes while reporting a provider failure for another approved item.</summary>
    [Fact]
    public async Task TicketCreationReturnsPerItemFailuresWithoutLosingSuccesses()
    {
        var store = new InMemoryRepositoryStore();
        using var factory = CreateFactory(store, new FailingSecondTaskProvider());
        using var client = factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/meetings", new
        {
            title = "Partial tickets",
            date = "2026-11-17T00:00:00Z",
            attendees = new[] { "Priya", "Alex" },
            transcript = "Priya, can you review the rate limits by 2099-12-01?\nAlex, can you check rollout by 2099-12-02?"
        });
        using var meetingDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var meetingId = meetingDocument.RootElement.GetProperty("id").GetGuid();
        var itemIds = meetingDocument.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToArray();
        foreach (var itemId in itemIds)
        {
            (await client.PostAsync($"/api/items/{itemId}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var ticketResponse = await client.PostAsync($"/api/meetings/{meetingId}/create-tickets", null);

        ticketResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var ticketDocument = JsonDocument.Parse(await ticketResponse.Content.ReadAsStringAsync());
        ticketDocument.RootElement.GetProperty("createdItems").GetArrayLength().Should().Be(1);
        ticketDocument.RootElement.GetProperty("failures").GetArrayLength().Should().Be(1);
        ticketDocument.RootElement.GetProperty("createdItems")[0].GetProperty("externalTicketId").GetString().Should().Be("TEST-1");

        using var itemsDocument = JsonDocument.Parse(await client.GetStringAsync($"/api/meetings/{meetingId}/items"));
        itemsDocument.RootElement.EnumerateArray().Any(item =>
            item.GetProperty("status").GetString() == "Approved" && item.GetProperty("externalTicketId").ValueKind == JsonValueKind.Null)
            .Should().BeTrue();
    }

    /// <summary>Returns a 400 ProblemDetails response when an action has no owner.</summary>
    [Fact]
    public async Task ApprovingDraftWithoutOwnerReturnsProblemDetails()
    {
        using var factory = CreateFactory(new InMemoryRepositoryStore());
        using var client = factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/meetings", new
        {
            title = "Missing owner",
            date = "2026-11-17T00:00:00Z",
            attendees = new[] { "Priya" },
            transcript = "I can review the release checklist by 2099-12-01."
        });
        using var meetingDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var itemId = meetingDocument.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();

        var approveResponse = await client.PostAsync($"/api/items/{itemId}/approve", null);

        approveResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        approveResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using var problem = JsonDocument.Parse(await approveResponse.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        problem.RootElement.GetProperty("errors").GetProperty("ownerName")[0].GetString().Should().Be("Missing owner");
    }

    /// <summary>Returns a 400 ProblemDetails response when an action has no due date.</summary>
    [Fact]
    public async Task ApprovingDraftWithoutDueDateReturnsProblemDetails()
    {
        using var factory = CreateFactory(new InMemoryRepositoryStore());
        using var client = factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/meetings", new
        {
            title = "Missing due date",
            date = "2026-11-17T00:00:00Z",
            attendees = new[] { "Priya" },
            transcript = "Priya, can you check the release checklist before the release?"
        });
        using var meetingDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var itemId = meetingDocument.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();

        var approveResponse = await client.PostAsync($"/api/items/{itemId}/approve", null);

        approveResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await approveResponse.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        problem.RootElement.GetProperty("errors").GetProperty("dueDate")[0].GetString().Should().Be("Missing due date");
    }

    /// <summary>Skips invalid approved items but creates tickets for valid items in the same meeting.</summary>
    [Fact]
    public async Task TicketCreationReturnsPrerequisiteFailuresAndCreatesValidBatchItems()
    {
        var store = new InMemoryRepositoryStore();
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/meetings", new
        {
            title = "Mixed prerequisites",
            date = "2026-11-17T00:00:00Z",
            attendees = new[] { "Priya" },
            transcript = "I can review the release checklist by 2099-12-01?\nPriya, can you check docs before the release?\nPriya, can you review the API by 2099-12-03?"
        });
        using var meetingDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var meetingId = meetingDocument.RootElement.GetProperty("id").GetGuid();

        foreach (var snapshot in store.Items)
        {
            snapshot.Status = ActionStatus.Approved;
            (await store.TryUpdateAsync(snapshot, snapshot.Version)).Should().BeTrue();
        }

        var ticketResponse = await client.PostAsync($"/api/meetings/{meetingId}/create-tickets", null);

        ticketResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var result = JsonDocument.Parse(await ticketResponse.Content.ReadAsStringAsync());
        result.RootElement.GetProperty("createdItems").GetArrayLength().Should().Be(1);
        result.RootElement.GetProperty("createdItems")[0].GetProperty("status").GetString().Should().Be("Created");
        result.RootElement.GetProperty("failures").GetArrayLength().Should().Be(2);
        result.RootElement.GetProperty("failures").EnumerateArray()
            .Select(failure => failure.GetProperty("detail").GetString())
            .Should().BeEquivalentTo("Missing owner", "Missing due date");
    }

    /// <summary>Creates an isolated in-memory test server with an optional task-provider fake.</summary>
    private static WebApplicationFactory<Program> CreateFactory(
        InMemoryRepositoryStore store,
        ITaskProvider? taskProvider = null) =>
        TestApiFactory.Create(store, taskProvider: taskProvider);

    /// <summary>Returns one test ticket and fails the next request to exercise partial successes.</summary>
    private sealed class FailingSecondTaskProvider : ITaskProvider
    {
        private int _callCount;

        /// <summary>Returns one test ticket and throws for the second item.</summary>
        public Task<string> CreateTaskAsync(ActionItem item, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Interlocked.Increment(ref _callCount) == 1
                ? Task.FromResult("TEST-1")
                : Task.FromException<string>(new InvalidOperationException("simulated provider failure"));
        }

        /// <summary>Returns no duplicate ticket candidates for this isolated integration provider.</summary>
        public Task<IReadOnlyList<string>> FindSimilarAsync(ActionItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        /// <summary>Returns no external state for this isolated integration provider.</summary>
        public Task<string?> GetStatusAsync(string ticketId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        /// <summary>Accepts due-date updates without external side effects in this test provider.</summary>
        public Task UpdateDueDateAsync(string ticketId, DateTime? dueDate, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
