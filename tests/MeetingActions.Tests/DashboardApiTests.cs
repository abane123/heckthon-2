using System.Net;
using System.Text.Json;
using FluentAssertions;
using MeetingActions.Core.Domain;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MeetingActions.Tests;

/// <summary>Verifies dashboard rates, null denominators, and active-work aggregation.</summary>
public sealed class DashboardApiTests
{
    /// <summary>Returns a null on-time rate when no completed items exist.</summary>
    [Fact]
    public async Task DashboardReturnsNullOnTimeRateWhenNothingIsCompleted()
    {
        using var factory = CreateFactory([
            new ActionItem
            {
                Title = "Current work",
                OwnerName = "Priya",
                DueDate = new DateTime(2026, 11, 24),
                Status = ActionStatus.Created
            }
        ]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var dashboard = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        dashboard.RootElement.GetProperty("onTimeRate").ValueKind.Should().Be(JsonValueKind.Null);
        dashboard.RootElement.GetProperty("slipRate").GetDouble().Should().Be(0);
    }

    /// <summary>Returns a null slip rate when completed items have no due dates.</summary>
    [Fact]
    public async Task DashboardReturnsNullSlipRateWhenNoItemsHaveDueDates()
    {
        using var factory = CreateFactory([
            new ActionItem
            {
                Title = "Completed without a deadline",
                OwnerName = "Alex",
                Status = ActionStatus.Done
            }
        ]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var dashboard = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        dashboard.RootElement.GetProperty("onTimeRate").GetDouble().Should().Be(100);
        dashboard.RootElement.GetProperty("slipRate").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>Counts only approved and active workflow statuses in owner workload metrics.</summary>
    [Fact]
    public async Task DashboardCountsOnlyActiveWorkStatuses()
    {
        using var factory = CreateFactory([
            new ActionItem { Title = "Draft item", OwnerName = "Priya", Status = ActionStatus.Draft },
            new ActionItem { Title = "Rejected item", OwnerName = "Alex", Status = ActionStatus.Rejected, DueDate = new DateTime(2026, 11, 25) },
            new ActionItem { Title = "Done item", OwnerName = "Jordan", Status = ActionStatus.Done, DueDate = new DateTime(2026, 11, 25) },
            new ActionItem { Title = "Approved item", OwnerName = "Priya", Status = ActionStatus.Approved, DueDate = new DateTime(2026, 11, 20), SlipCount = 1 },
            new ActionItem { Title = "Created item", OwnerName = "Alex", Status = ActionStatus.Created, DueDate = new DateTime(2026, 11, 25) },
            new ActionItem { Title = "In-progress item", OwnerName = "Alex", Status = ActionStatus.InProgress, DueDate = new DateTime(2026, 11, 25) },
            new ActionItem { Title = "Overdue item", OwnerName = "Jordan", Status = ActionStatus.Overdue, DueDate = new DateTime(2026, 11, 20), SlipCount = 1 },
            new ActionItem { Title = "Escalated item", OwnerName = "Priya", Status = ActionStatus.Escalated, DueDate = new DateTime(2026, 11, 19), SlipCount = 2 }
        ]);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/dashboard");

        using var dashboard = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var openByOwner = dashboard.RootElement.GetProperty("openItemsPerOwner");
        openByOwner.GetProperty("Priya").GetInt32().Should().Be(2);
        openByOwner.GetProperty("Alex").GetInt32().Should().Be(2);
        openByOwner.GetProperty("Jordan").GetInt32().Should().Be(1);
        openByOwner.EnumerateObject().Should().HaveCount(3);
        dashboard.RootElement.GetProperty("slipRate").GetDouble().Should().BeApproximately(300d / 7, 0.001);
    }

    /// <summary>Creates an isolated in-memory host with the supplied dashboard fixture.</summary>
    private static WebApplicationFactory<Program> CreateFactory(IReadOnlyCollection<ActionItem> items)
    {
        var store = new InMemoryRepositoryStore();
        var meeting = new Meeting { Title = "Dashboard test", MeetingDate = new DateTime(2026, 11, 23) };
        meeting.Items = items.ToList();
        foreach (var item in meeting.Items)
        {
            item.MeetingId = meeting.Id;
        }

        store.AddMeetingAsync(meeting).GetAwaiter().GetResult();
        return TestApiFactory.Create(store);
    }
}
