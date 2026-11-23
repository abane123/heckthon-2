using System.Net;
using System.Text.Json;
using FluentAssertions;
using MeetingActions.Core.Abstractions;
using MeetingActions.Infrastructure.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeetingActions.Tests;

/// <summary>Verifies Ollama request construction, structured parsing, retries, and chunk handling.</summary>
public sealed class OllamaExtractorTests
{
    private const string ValidOutput = """
        {
          "items": [{
            "title": "Review the launch checklist",
            "owner_name": "Priya",
            "due_date_text": "by Friday",
            "confidence": 0.87,
            "source_quote": "Priya will review the launch checklist by Friday.",
            "timestamp": "03:18",
            "is_commitment": true,
            "needs_input": []
          }],
          "decisions": ["Keep the rollout window unchanged."]
        }
        """;
    private const string EmptyOutput = """{"items":[],"decisions":[]}""";

    /// <summary>Confirms the embedded prompt is loaded with its required language and extraction sections.</summary>
    [Fact]
    public void EmbeddedSystemPromptContainsRequiredSectionHeadings()
    {
        foreach (var heading in new[]
        {
            "ROLE",
            "INPUT",
            "LANGUAGES",
            "TRANSCRIPT FORMATS",
            "SPEECH-TO-TEXT NOISE",
            "WHAT IS AN ACTION ITEM",
            "OWNER (owner_name)",
            "DUE DATE (due_date_text)",
            "SOURCE QUOTE (source_quote)",
            "OUTPUT RULES",
            "EXAMPLES"
        })
        {
            OllamaExtractor.SystemPrompt.Should().Contain(heading);
        }
    }

    /// <summary>Parses a valid response and verifies the schema-constrained Ollama request payload.</summary>
    [Fact]
    public async Task ExtractorParsesStructuredResponseAndSendsConfiguredRequest()
    {
        string? requestBody = null;
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return ChatResponse(ValidOutput);
        });
        var extractor = CreateExtractor(handler);

        var result = await extractor.ExtractAsync(
            "Priya will review the launch checklist by Friday.",
            new DateTime(2026, 11, 17),
            ["Priya"],
            meetingTitle: "Release readiness");

        result.Items.Should().ContainSingle();
        result.Items[0].Title.Should().Be("Review the launch checklist");
        result.Items[0].OwnerName.Should().Be("Priya");
        result.Items[0].DueDateText.Should().Be("by Friday");
        result.Items[0].Timestamp.Should().Be("03:18");

        using var request = JsonDocument.Parse(requestBody!);
        var body = request.RootElement;
        body.GetProperty("model").GetString().Should().Be("llama3.1:8b");
        body.GetProperty("stream").GetBoolean().Should().BeFalse();
        body.GetProperty("keep_alive").GetString().Should().Be("10m");
        body.GetProperty("options").GetProperty("num_ctx").GetInt32().Should().Be(16384);
        body.GetProperty("options").GetProperty("temperature").GetDouble().Should().Be(0);
        body.GetProperty("format").GetProperty("properties").GetProperty("items").ValueKind
            .Should().Be(JsonValueKind.Object);
        body.GetProperty("messages")[1].GetProperty("content").GetString()
            .Should().Contain("Meeting title: Release readiness");
        body.GetProperty("messages")[0].GetProperty("content").GetString()
            .Should().Contain("Copy it verbatim");
    }

    /// <summary>Uses the more conservative Devanagari token estimate to bound chunks by NumCtx.</summary>
    [Fact]
    public async Task ExtractorUsesScriptSensitiveTokenBudgetForDevanagariChunks()
    {
        var latinRequests = new List<string>();
        var devanagariRequests = new List<string>();
        var options = new OllamaOptions { NumCtx = 12000, MaxTranscriptCharsPerChunk = 12000 };
        var latinExtractor = CreateExtractor(
            new FakeHttpMessageHandler(async (request, cancellationToken) =>
            {
                latinRequests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return ChatResponse(EmptyOutput);
            }),
            options);
        var devanagariExtractor = CreateExtractor(
            new FakeHttpMessageHandler(async (request, cancellationToken) =>
            {
                devanagariRequests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return ChatResponse(EmptyOutput);
            }),
            options);
        var latinTranscript = string.Join(Environment.NewLine,
            Enumerable.Range(1, 3).Select(index => $"Speaker {index}: {new string('a', 1800)}"));
        var devanagariTranscript = string.Join(Environment.NewLine,
            Enumerable.Range(1, 3).Select(index => $"Speaker {index}: {new string('क', 1800)}"));

        await latinExtractor.ExtractAsync(latinTranscript, new DateTime(2026, 11, 17), ["Priya"]);
        var result = await devanagariExtractor.ExtractAsync(
            devanagariTranscript,
            new DateTime(2026, 11, 17),
            ["Priya"]);

        latinRequests.Should().ContainSingle();
        devanagariRequests.Should().HaveCountGreaterThan(1);
        result.ChunkCount.Should().Be(devanagariRequests.Count);
        devanagariRequests.Select(ReadTranscriptFromRequest)
            .Should().OnlyContain(chunk => chunk.Length <= options.MaxTranscriptCharsPerChunk);
    }

    /// <summary>Surfaces a warning when Ollama reports prompt evaluation close to the context limit.</summary>
    [Fact]
    public async Task ExtractorWarnsWhenPromptEvaluationApproachesNumCtx()
    {
        var extractor = CreateExtractor(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(ChatResponse(EmptyOutput, promptEvaluationCount: 16000))));

        var result = await extractor.ExtractAsync(
            "Priya will review the launch checklist by Friday.",
            new DateTime(2026, 11, 17),
            ["Priya"]);

        result.Warnings.Should().ContainSingle()
            .Which.Should().Contain("may have been truncated");
    }

    /// <summary>Retries malformed model content once with the required JSON-only nudge.</summary>
    [Fact]
    public async Task ExtractorRetriesInvalidJsonOnceAndThenReturnsParsedItems()
    {
        var requests = new List<string>();
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return ChatResponse(requests.Count == 1 ? "not json" : ValidOutput);
        });
        var extractor = CreateExtractor(handler);

        var result = await extractor.ExtractAsync(
            "Priya will review the launch checklist by Friday.",
            new DateTime(2026, 11, 17),
            ["Priya"]);

        result.Items.Should().ContainSingle();
        requests.Should().HaveCount(2);
        using var retry = JsonDocument.Parse(requests[1]);
        retry.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()
            .Should().Contain("Your previous answer was not valid, return only JSON.");
    }

    /// <summary>Maps Ollama's missing-model response to an actionable provider exception.</summary>
    [Fact]
    public async Task ExtractorMapsChatNotFoundToModelPullGuidance()
    {
        var extractor = CreateExtractor(new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));

        var action = () => extractor.ExtractAsync(
            "Priya will review the launch checklist by Friday.",
            new DateTime(2026, 11, 17),
            ["Priya"]);

        await action.Should().ThrowAsync<LlmUnavailableException>()
            .WithMessage("*ollama pull llama3.1:8b*");
    }

    /// <summary>Converts a request timeout into an actionable typed provider exception.</summary>
    [Fact]
    public async Task ExtractorMapsTimeoutToTypedProviderException()
    {
        var options = new OllamaOptions { RequestTimeoutSeconds = 1 };
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ChatResponse(ValidOutput);
        });
        var extractor = CreateExtractor(handler, options);

        var action = () => extractor.ExtractAsync(
            "Priya will review the launch checklist by Friday.",
            new DateTime(2026, 11, 17),
            ["Priya"]);

        await action.Should().ThrowAsync<LlmUnavailableException>().WithMessage("*timed out*");
    }

    /// <summary>Chunks at transcript lines and collapses the duplicated action in overlapping context.</summary>
    [Fact]
    public async Task ExtractorChunksAtSpeakerLinesAndMergesOverlapDuplicates()
    {
        const string repeatedQuote = "Alex will verify smoke tests.";
        const string responseBody = """
            {
              "items": [{
                "title": "Verify smoke tests",
                "owner_name": "Alex",
                "due_date_text": null,
                "confidence": 0.8,
                "source_quote": "Alex will verify smoke tests.",
                "timestamp": null,
                "is_commitment": true,
                "needs_input": ["due date"]
              }],
              "decisions": []
            }
            """;
        var requests = new List<string>();
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return ChatResponse(responseBody);
        });
        var extractor = CreateExtractor(handler, new OllamaOptions { MaxTranscriptCharsPerChunk = 75 });
        var transcript = string.Join(Environment.NewLine,
            "Priya will review the launch checklist.",
            repeatedQuote,
            "Jordan will prepare the release notes.");

        var result = await extractor.ExtractAsync(transcript, new DateTime(2026, 11, 17), ["Alex"]);

        requests.Should().HaveCount(2);
        result.Items.Should().ContainSingle().Which.SourceQuote.Should().Be(repeatedQuote);
        var transcriptChunks = requests.Select(ReadTranscriptFromRequest).ToArray();
        transcriptChunks.Should().OnlyContain(chunk => chunk.Length <= 75);
        transcriptChunks[0].Should().Contain(repeatedQuote);
        transcriptChunks[1].Should().Contain(repeatedQuote);
    }

    /// <summary>Exercises the local configured Ollama model only when explicitly enabled.</summary>
    [OllamaLiveFact]
    public async Task LiveOllamaAnalysisReturnsVerifiableQuotesAndResolvedOwner()
    {
        var repositoryRoot = FindRepositoryRoot();
        var transcript = await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "samples", "meeting-transcript.txt"));
        var settings = new OllamaOptions
        {
            BaseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434",
            Model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "llama3.1:8b"
        };
        var extractor = new OllamaExtractor(
            new LiveHttpClientFactory(settings.BaseUrl),
            Options.Create(settings),
            NullLogger<OllamaExtractor>.Instance);

        var result = await extractor.ExtractAsync(
            transcript,
            new DateTime(2026, 11, 17),
            ["Priya", "Alex", "Jordan"],
            meetingTitle: "Release Readiness");

        result.Items.Should().NotBeEmpty();
        result.Items.Should().OnlyContain(item => ContainsQuote(transcript, item.SourceQuote));
        result.Items.Should().Contain(item => item.OwnerName != null);
    }

    /// <summary>Finds the repository root without depending on the test process working directory.</summary>
    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MeetingActions.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate MeetingActions.sln for the live Ollama test.");
    }

    /// <summary>Verifies a quote after applying the extractor's whitespace-insensitive matching convention.</summary>
    private static bool ContainsQuote(string transcript, string quote)
    {
        static string Normalize(string value) =>
            string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .ToUpperInvariant();

        return Normalize(transcript).Contains(Normalize(quote), StringComparison.Ordinal);
    }

    /// <summary>Builds an extractor with a deterministic fake HTTP transport and supplied settings.</summary>
    private static OllamaExtractor CreateExtractor(
        FakeHttpMessageHandler handler,
        OllamaOptions? settings = null) =>
        new(
            new FakeHttpClientFactory(handler),
            Options.Create(settings ?? new OllamaOptions()),
            NullLogger<OllamaExtractor>.Instance);

    /// <summary>Creates a successful chat envelope with optional prompt-token telemetry.</summary>
    private static HttpResponseMessage ChatResponse(
        string structuredContent,
        int? promptEvaluationCount = null) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                message = new { content = structuredContent },
                prompt_eval_count = promptEvaluationCount
            }))
        };

    /// <summary>Reads the transcript portion of a captured request for chunk-boundary assertions.</summary>
    private static string ReadTranscriptFromRequest(string requestBody)
    {
        using var request = JsonDocument.Parse(requestBody);
        var prompt = request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        return prompt[(prompt.IndexOf("Transcript chunk:", StringComparison.Ordinal)
            + "Transcript chunk:".Length)..].Trim();
    }

    /// <summary>Routes fake HTTP calls to a test delegate without opening a network connection.</summary>
    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>Dispatches a request to the configured response function.</summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    /// <summary>Creates factory clients using the supplied fake handler and Ollama base address.</summary>
    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        /// <summary>Returns a client that sends requests through the fake handler.</summary>
        public HttpClient CreateClient(string name)
        {
            _ = name;
            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:11434")
            };
        }
    }

    /// <summary>Creates a real local HTTP client for the opt-in Ollama integration test.</summary>
    private sealed class LiveHttpClientFactory(string baseUrl) : IHttpClientFactory
    {
        /// <summary>Returns an HTTP client targeting the configured local Ollama endpoint.</summary>
        public HttpClient CreateClient(string name)
        {
            _ = name;
            return new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute) };
        }
    }
}

/// <summary>Runs a live provider test only when the user explicitly opts in.</summary>
public sealed class OllamaLiveFactAttribute : FactAttribute
{
    /// <summary>Marks the live test skipped unless OLLAMA_LIVE_TEST is exactly set to 1.</summary>
    public OllamaLiveFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("OLLAMA_LIVE_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Skipped: set OLLAMA_LIVE_TEST=1 to call the locally configured Ollama model.";
        }
    }
}
