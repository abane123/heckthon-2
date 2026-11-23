using System.Net;
using System.Text.Json;
using FluentAssertions;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Jobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MeetingActions.Tests;

/// <summary>Verifies the development-only reset endpoint and its deadline demo scenario.</summary>
public sealed class DevResetApiTests
{
    /// <summary>Resets all records and verifies the seeded attendees, tickets, statuses, and relative dates.</summary>
    [Fact]
    public async Task ResetCreatesTheExpectedDemoScenario()
    {
        var store = new InMemoryRepositoryStore();
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();

        var resetResponse = await client.PostAsync("/api/dev/reset", null);

        resetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var resetDocument = JsonDocument.Parse(await resetResponse.Content.ReadAsStringAsync());
        resetDocument.RootElement.GetProperty("actionItemCount").GetInt32().Should().Be(5);
        var currentDate = resetDocument.RootElement.GetProperty("currentDate").GetDateTime().Date;
        currentDate.Should().Be(DateTime.UtcNow.Date);
        using var meetingsDocument = JsonDocument.Parse(await client.GetStringAsync("/api/meetings"));
        meetingsDocument.RootElement.GetArrayLength().Should().Be(1);
        meetingsDocument.RootElement[0].GetProperty("attendees").EnumerateArray()
            .Select(attendee => attendee.GetString()).Should().BeEquivalentTo("Priya", "Alex", "Jordan");

        var items = store.Items.ToArray();
        items.Should().HaveCount(5);
        items.Select(item => item.Status.ToString())
            .Should().BeEquivalentTo("Created", "InProgress", "Overdue", "Overdue", "Done");
        items.Select(item => item.ExternalTicketId)
            .All(ticketId => ticketId is not null && ticketId.StartsWith("ADO-DEMO-", StringComparison.Ordinal))
            .Should().BeTrue();
        items.Single(item => item.Status == ActionStatus.Created).DueDate!.Value.Date.Should().Be(currentDate.AddDays(1));
        items.Single(item => item.Status == ActionStatus.InProgress).DueDate!.Value.Date.Should().Be(currentDate);
        items.Where(item => item.Status == ActionStatus.Overdue)
            .Select(item => item.SlipCount).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    /// <summary>Runs follow-up processing immediately after reset and checks reminders, nudges, and escalation.</summary>
    [Fact]
    public async Task FollowUpRunOnSeededScenarioProducesReminderNudgesAndEscalation()
    {
        var store = new InMemoryRepositoryStore();
        using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        (await client.PostAsync("/api/dev/reset", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        int remindersSent;
        using (var scope = factory.Services.CreateScope())
        {
            remindersSent = await scope.ServiceProvider.GetRequiredService<FollowUpService>().RunAsync();
        }

        remindersSent.Should().Be(1);
        var channels = store.FollowUps.Select(followUp => followUp.Channel).ToArray();
        channels.Count(channel => channel == "reminder").Should().Be(1);
        channels.Count(channel => channel == "ticket-status-check").Should().Be(3);
        channels.Count(channel => channel == "overdue").Should().Be(3);
        channels.Count(channel => channel == "escalation").Should().Be(2);
        store.Items.Single(item => item.ExternalTicketId == "ADO-DEMO-003").Status.Should().Be(ActionStatus.Escalated);
        store.Items.Single(item => item.ExternalTicketId == "ADO-DEMO-004").Status.Should().Be(ActionStatus.Escalated);
        store.Items.Single(item => item.ExternalTicketId == "ADO-DEMO-005").Status.Should().Be(ActionStatus.Done);
    }

    /// <summary>Hides the reset endpoint with a 404 response outside Development.</summary>
    [Fact]
    public async Task ResetEndpointReturnsNotFoundOutsideDevelopment()
    {
        var store = new InMemoryRepositoryStore();
        using var factory = CreateFactory(store, "Production");
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/dev/reset", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Creates an isolated in-memory test API host with the requested environment.</summary>
    private static WebApplicationFactory<Program> CreateFactory(
        InMemoryRepositoryStore store,
        string environment = "Development") =>
        TestApiFactory.Create(store, environment);
}
