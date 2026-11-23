using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeetingActions.Tests;

/// <summary>Verifies health, virtual-clock, unapproval, and RFC 7807 API behavior.</summary>
public sealed class ApiSystemEndpointsTests
{
    /// <summary>Returns the API status, version, and UTC timestamp.</summary>
    [Fact]
    public async Task HealthEndpointReturnsRuntimeMetadata()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        document.RootElement.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        DateTime.TryParse(document.RootElement.GetProperty("utcTime").GetString(), out _).Should().BeTrue();
    }

    /// <summary>Returns the current virtual clock value to the client.</summary>
    [Fact]
    public async Task ClockEndpointReturnsCurrentVirtualDate()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/dev/clock");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        DateTime.TryParse(document.RootElement.GetProperty("currentDate").GetString(), out _).Should().BeTrue();
    }

    /// <summary>Allows unapproval only for an item that is currently approved.</summary>
    [Fact]
    public async Task UnapproveEndpointAcceptsApprovedItemsAndRejectsOtherStatuses()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var itemId = await CreateDraftItemAsync(client);

        (await client.PostAsync($"/api/items/{itemId}/unapprove", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsync($"/api/items/{itemId}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var unapproveResponse = await client.PostAsync($"/api/items/{itemId}/unapprove", null);
        unapproveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await unapproveResponse.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("status").GetString().Should().Be("Draft");
        (await client.PostAsync($"/api/items/{itemId}/unapprove", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>Returns traceable ProblemDetails for validation and unexpected exceptions.</summary>
    [Fact]
    public async Task ValidationAndUnhandledErrorsReturnProblemDetails()
    {
        using (var validationFactory = CreateFactory())
        using (var validationClient = validationFactory.CreateClient())
        {
            using var invalidJson = new StringContent("{", Encoding.UTF8, "application/json");
            var validationResponse = await validationClient.PostAsync("/api/meetings", invalidJson);
            validationResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            await AssertProblemDetailsAsync(validationResponse, "One or more validation errors occurred.");
        }

        using (var exceptionFactory = CreateFactory(throwFromExtractor: true))
        using (var exceptionClient = exceptionFactory.CreateClient())
        {
            var exceptionResponse = await exceptionClient.PostAsJsonAsync("/api/meetings", new
            {
                title = "Failure case",
                date = "2026-11-17T00:00:00Z",
                attendees = new[] { "Priya" },
                transcript = "Priya, can you check the limits?"
            });
            exceptionResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            await AssertProblemDetailsAsync(exceptionResponse, "An unexpected error occurred.");
            var body = await exceptionResponse.Content.ReadAsStringAsync();
            body.Should().NotContain("InvalidOperationException").And.NotContain("StackTrace");
        }
    }

    /// <summary>Creates a draft action through the public meeting API and returns its identifier.</summary>
    private static async Task<Guid> CreateDraftItemAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/meetings", new
        {
            title = "Unapprove test",
            date = "2026-11-17T00:00:00Z",
            attendees = new[] { "Priya" },
            transcript = "Priya, can you review the limits by 2099-12-01?"
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();
    }

    /// <summary>Checks the standard ProblemDetails fields and mandatory trace identifier.</summary>
    private static async Task AssertProblemDetailsAsync(HttpResponseMessage response, string title)
    {
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var problem = document.RootElement;
        problem.GetProperty("status").GetInt32().Should().Be((int)response.StatusCode);
        problem.GetProperty("title").GetString().Should().Be(title);
        problem.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>Creates an isolated test server with an optional failing extractor.</summary>
    private static WebApplicationFactory<Program> CreateFactory(bool throwFromExtractor = false)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"meeting-actions-{Guid.NewGuid():N}.db");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = $"Data Source={databasePath}",
                    ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
                    ["Cors:AllowedOrigins:1"] = "http://127.0.0.1:5173"
                }));
            if (throwFromExtractor)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ILlmExtractor>();
                    services.AddSingleton<ILlmExtractor, ThrowingLlmExtractor>();
                });
            }
        });
        return factory;
    }

    /// <summary>Throws intentionally so API exception middleware can be exercised.</summary>
    private sealed class ThrowingLlmExtractor : ILlmExtractor
    {
        /// <summary>Raises a test exception instead of extracting candidates.</summary>
        public Task<IReadOnlyList<ActionItem>> ExtractAsync(string transcript, DateTime meetingDate,
            IReadOnlyList<string> attendees, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Test-only failure");
    }
}
