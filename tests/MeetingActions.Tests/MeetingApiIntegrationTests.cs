using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeetingActions.Tests;

/// <summary>Exercises the meeting-create, approve, and ticket workflow over HTTP.</summary>
public sealed class MeetingApiIntegrationTests
{
    /// <summary>Creates a meeting, approves its extracted item, and creates a ticket.</summary>
    [Fact]
    public async Task MeetingCanBeCreatedApprovedAndConvertedToTicket()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"meeting-actions-{Guid.NewGuid():N}.db");
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = $"Data Source={databasePath}",
                    }));
            });
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

            var ticketResponse = await client.PostAsync(
                $"/api/meetings/{meetingDocument.RootElement.GetProperty("id").GetGuid()}/create-tickets", null);
            ticketResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var ticketDocument = JsonDocument.Parse(await ticketResponse.Content.ReadAsStringAsync());
            ticketDocument.RootElement.GetProperty("createdItems")[0].GetProperty("externalTicketId").GetString().Should().StartWith("ADO-");
            ticketDocument.RootElement.GetProperty("createdItems")[0].GetProperty("status").GetString().Should().Be("Created");
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    /// <summary>Restores a rejected action through the API and verifies its draft status.</summary>
    [Fact]
    public async Task RejectedActionCanBeRestoredForReview()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"meeting-actions-{Guid.NewGuid():N}.db");
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = $"Data Source={databasePath}",
                    })));
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
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    /// <summary>Persists ticket successes while reporting a provider failure for another approved item.</summary>
    [Fact]
    public async Task TicketCreationReturnsPerItemFailuresWithoutLosingSuccesses()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"meeting-actions-{Guid.NewGuid():N}.db");
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = $"Data Source={databasePath}",
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ITaskProvider>();
                    services.AddSingleton<ITaskProvider, FailingSecondTaskProvider>();
                });
            });
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
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    /// <summary>Returns a ticket for the first item and fails the next request for partial-result coverage.</summary>
    private sealed class FailingSecondTaskProvider : ITaskProvider
    {
        private int callCount;

        /// <summary>Returns one test ticket and throws for the second item.</summary>
        public Task<string> CreateTaskAsync(ActionItem item, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref callCount) == 1)
            {
                return Task.FromResult("TEST-1");
            }

            return Task.FromException<string>(new InvalidOperationException("simulated provider failure"));
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
