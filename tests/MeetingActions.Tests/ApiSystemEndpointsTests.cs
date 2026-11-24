using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Core.Services;
using MeetingActions.Infrastructure.Llm;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        document.RootElement.GetProperty("llm").GetProperty("status").GetString().Should().Be("Healthy");
    }

    /// <summary>Reports the active built-in provider through the LLM information endpoint.</summary>
    [Fact]
    public async Task LlmInfoEndpointReturnsStubProviderDetails()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/llm/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("provider").GetString().Should().Be("Stub");
        document.RootElement.GetProperty("model").GetString().Should().Be("Built-in rules");
        document.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        document.RootElement.GetProperty("message").GetString().Should().Contain("built-in rules");
    }

    /// <summary>Keeps overall health successful while identifying an unavailable Ollama provider.</summary>
    [Fact]
    public async Task HealthAndLlmInfoReportOnlyTheLlmPartAsDegraded()
    {
        using var factory = TestApiFactory.Create(
            new InMemoryRepositoryStore(),
            extractor: new MeetingActions.Infrastructure.Integrations.StubLlmExtractor(),
            llmProvider: "Ollama",
            llmHealth: new FakeLlmHealth(
                new LlmHealthResult(false, false, "llama3.1:8b", "Cannot reach Ollama at http://localhost:11434. Is it running?")));
        using var client = factory.CreateClient();

        var healthResponse = await client.GetAsync("/api/health");
        using var healthDocument = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
        healthDocument.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        healthDocument.RootElement.GetProperty("llm").GetProperty("status").GetString().Should().Be("Degraded");

        var infoResponse = await client.GetAsync("/api/llm/info");
        using var infoDocument = JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync());
        infoDocument.RootElement.GetProperty("provider").GetString().Should().Be("Ollama");
        infoDocument.RootElement.GetProperty("status").GetString().Should().Be("Degraded");
        infoDocument.RootElement.GetProperty("message").GetString().Should().Contain("Is it running?");
    }

    /// <summary>Starts with Ollama selected and returns degraded health and info within the bounded probe timeout.</summary>
    [Fact]
    public async Task HostAndHealthEndpointsDoNotWaitForUnresponsiveOllama()
    {
        using var handler = new NeverRespondingOllamaHandler();
        var health = new OllamaLlmHealth(
            new NeverRespondingHttpClientFactory(handler),
            Options.Create(new OllamaOptions
            {
                BaseUrl = "http://localhost:11434",
                RequestTimeoutSeconds = 180
            }));
        using var factory = TestApiFactory.Create(
            new InMemoryRepositoryStore(),
            extractor: new NeverCallLlmExtractor(),
            llmProvider: "Ollama",
            llmHealth: health);
        var startupTimer = Stopwatch.StartNew();
        using var client = factory.CreateClient();
        startupTimer.Stop();
        startupTimer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));

        var healthTimer = Stopwatch.StartNew();
        var healthResponse = await client.GetAsync("/api/health");
        healthTimer.Stop();
        healthTimer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        using var healthDocument = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
        healthDocument.RootElement.GetProperty("llm").GetProperty("status").GetString().Should().Be("Degraded");

        var infoTimer = Stopwatch.StartNew();
        var infoResponse = await client.GetAsync("/api/llm/info");
        infoTimer.Stop();
        infoTimer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        using var infoDocument = JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync());
        infoDocument.RootElement.GetProperty("status").GetString().Should().Be("Degraded");
        infoDocument.RootElement.GetProperty("message").GetString().Should().Contain("timed out");
        handler.RequestCount.Should().Be(2);
        handler.Paths.Should().OnlyContain(path => path == "/api/tags");
    }

    /// <summary>Seeds the development store with Ollama selected without invoking an unreachable LLM extractor.</summary>
    [Fact]
    public async Task StartupSeedingUsesDeterministicExtractorWhenOllamaIsConfigured()
    {
        var store = new InMemoryRepositoryStore();
        using var factory = TestApiFactory.Create(
            store,
            extractor: new NeverCallLlmExtractor(),
            llmProvider: "Ollama",
            llmHealth: new FakeLlmHealth(
                new LlmHealthResult(false, false, "llama3.1:8b", "Cannot reach Ollama.")));
        using var client = factory.CreateClient();

        var meetings = await store.GetMeetingsAsync();

        meetings.Should().ContainSingle();
        meetings[0].Items.Should().NotBeEmpty();
        meetings[0].Analysis!.Provider.Should().Be("Stub");
    }

    /// <summary>Returns fallback analysis metadata when Ollama is unavailable and fallback is enabled.</summary>
    [Fact]
    public async Task CreateMeetingUsesStubFallbackWhenConfigured()
    {
        var fallback = new FallbackLlmExtractor(
            new UnavailableLlmExtractor(),
            new MeetingActions.Infrastructure.Integrations.StubLlmExtractor(),
            Options.Create(new LlmOptions { Provider = "Ollama", FallbackToStub = true }),
            NullLogger<FallbackLlmExtractor>.Instance);
        using var factory = TestApiFactory.Create(
            new InMemoryRepositoryStore(),
            extractor: fallback,
            llmProvider: "Ollama",
            llmHealth: new FakeLlmHealth(
                new LlmHealthResult(false, false, "llama3.1:8b", "Cannot reach Ollama.")),
            llmFallbackToStub: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/meetings", new
        {
            title = "Fallback release",
            date = "2026-11-17T00:00:00Z",
            attendees = new[] { "Priya" },
            transcript = "Priya, can you review the release checklist by 2099-12-01?"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("analysis").GetProperty("provider").GetString()
            .Should().Be("Stub (fallback)");
    }

    /// <summary>Returns non-fatal provider warnings in the optional meeting analysis metadata.</summary>
    [Fact]
    public async Task CreateMeetingIncludesAnalysisWarnings()
    {
        using var factory = TestApiFactory.Create(
            new InMemoryRepositoryStore(),
            extractor: new WarningLlmExtractor());
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/meetings", new
        {
            title = "Context warning",
            date = "2026-11-17T00:00:00Z",
            attendees = new[] { "Priya" },
            transcript = "Priya will review the launch checklist by Friday."
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("analysis").GetProperty("warnings")
            .EnumerateArray().Single().GetString().Should().Contain("may have been truncated");
    }

    /// <summary>Returns an actionable 503 ProblemDetails response when Ollama fails without fallback.</summary>
    [Fact]
    public async Task CreateMeetingReturnsProviderProblemWhenFallbackIsDisabled()
    {
        var extractor = new FallbackLlmExtractor(
            new UnavailableLlmExtractor(),
            new MeetingActions.Infrastructure.Integrations.StubLlmExtractor(),
            Options.Create(new LlmOptions { Provider = "Ollama", FallbackToStub = false }),
            NullLogger<FallbackLlmExtractor>.Instance);
        using var factory = TestApiFactory.Create(
            new InMemoryRepositoryStore(),
            extractor: extractor,
            llmProvider: "Ollama",
            llmHealth: new FakeLlmHealth(
                new LlmHealthResult(false, false, "llama3.1:8b", "Cannot reach Ollama.")));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/meetings", new
        {
            title = "No fallback release",
            date = "2026-11-17T00:00:00Z",
            attendees = new[] { "Priya" },
            transcript = "Priya, can you review the release checklist by 2099-12-01?"
        });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("detail").GetString().Should().Be("Cannot reach Ollama.");
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

    /// <summary>Creates an isolated in-memory test server with an optional failing extractor.</summary>
    private static WebApplicationFactory<Program> CreateFactory(bool throwFromExtractor = false) =>
        TestApiFactory.Create(
            new InMemoryRepositoryStore(),
            extractor: throwFromExtractor ? new ThrowingLlmExtractor() : null);

    /// <summary>Throws only for the target request so startup seeding can finish in API tests.</summary>
    private sealed class ThrowingLlmExtractor : ILlmExtractor
    {
        /// <summary>Raises a test exception for the limits transcript and otherwise returns no candidates.</summary>
        public Task<AnalysisResult> ExtractAsync(
            string transcript,
            DateTime meetingDate,
            IReadOnlyList<string> attendees,
            CancellationToken cancellationToken = default,
            string meetingTitle = "")
        {
            if (transcript.Contains("check the limits", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Test-only failure");
            }

            return Task.FromResult(new AnalysisResult([], [], 0, "Stub", "Built-in rules", 0, 1));
        }

    }

    /// <summary>Returns a deterministic warning to verify API serialization.</summary>
    private sealed class WarningLlmExtractor : ILlmExtractor
    {
        /// <summary>Produces an empty analysis with a context-truncation warning.</summary>
        public Task<AnalysisResult> ExtractAsync(
            string transcript,
            DateTime meetingDate,
            IReadOnlyList<string> attendees,
            CancellationToken cancellationToken = default,
            string meetingTitle = "") =>
            Task.FromResult(new AnalysisResult(
                [],
                [],
                0,
                "Ollama",
                "test-model",
                5,
                1,
                ["Ollama prompt may have been truncated."]));
    }

    /// <summary>Fails if startup seeding or a health endpoint attempts transcript extraction.</summary>
    private sealed class NeverCallLlmExtractor : ILlmExtractor
    {
        /// <summary>Fails immediately because this extractor must never be needed during startup.</summary>
        public Task<AnalysisResult> ExtractAsync(
            string transcript,
            DateTime meetingDate,
            IReadOnlyList<string> attendees,
            CancellationToken cancellationToken = default,
            string meetingTitle = "") =>
            Task.FromException<AnalysisResult>(
                new InvalidOperationException("Startup unexpectedly invoked the configured LLM extractor."));
    }

    /// <summary>Provides an Ollama health client factory backed by a deliberately non-responding handler.</summary>
    private sealed class NeverRespondingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        /// <summary>Creates a client that sends its requests through the fake Ollama handler.</summary>
        public HttpClient CreateClient(string name)
        {
            _ = name;
            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:11434"),
                Timeout = Timeout.InfiniteTimeSpan
            };
        }
    }

    /// <summary>Records tags probes and waits until the caller's health timeout cancels them.</summary>
    private sealed class NeverRespondingOllamaHandler : HttpMessageHandler
    {
        private readonly List<string> _paths = [];
        private readonly object _gate = new();
        private int _requestCount;

        /// <summary>Gets the number of Ollama requests made by the health check.</summary>
        public int RequestCount => Volatile.Read(ref _requestCount);

        /// <summary>Gets a snapshot of requested Ollama resource paths.</summary>
        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_gate)
                {
                    return _paths.ToArray();
                }
            }
        }

        /// <summary>Never returns a response and exits only when the health probe cancels its request.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            lock (_gate)
            {
                _paths.Add(request.RequestUri!.AbsolutePath);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The non-responding handler should only exit by cancellation.");
        }
    }

    /// <summary>Provides a fixed LLM health result for endpoint tests.</summary>
    private sealed class FakeLlmHealth(LlmHealthResult result) : ILlmHealth
    {
        /// <summary>Returns the configured result without making an external request.</summary>
        public Task<LlmHealthResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    /// <summary>Throws an actionable availability error without reaching a real Ollama server.</summary>
    private sealed class UnavailableLlmExtractor : ILlmExtractor
    {
        /// <summary>Returns a failed provider task for every transcript.</summary>
        public Task<AnalysisResult> ExtractAsync(
            string transcript,
            DateTime meetingDate,
            IReadOnlyList<string> attendees,
            CancellationToken cancellationToken = default,
            string meetingTitle = "") =>
            Task.FromException<AnalysisResult>(
                new LlmUnavailableException("Cannot reach Ollama."));
    }
}
