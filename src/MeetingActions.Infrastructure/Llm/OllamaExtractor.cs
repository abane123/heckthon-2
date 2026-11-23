using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeetingActions.Infrastructure.Llm;

/// <summary>Extracts structured action candidates by calling a local Ollama chat model.</summary>
public sealed class OllamaExtractor(
    IHttpClientFactory httpClientFactory,
    IOptions<OllamaOptions> options,
    ILogger<OllamaExtractor> logger) : ILlmExtractor
{
    private const string PromptResourceName =
        "MeetingActions.Infrastructure.Llm.Prompts.extraction-system-prompt.txt";

    /// <summary>Loads the evidence-first extraction rules from the embedded prompt resource.</summary>
    public static string SystemPrompt { get; } = LoadSystemPrompt();

    private const int OverlapLineCount = 2;
    private const int ReservedOutputTokens = 1500;
    private const double LatinCharactersPerToken = 4;
    private const double DevanagariCharactersPerToken = 1.5;
    private const double ContextWarningThreshold = 0.95;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    // Strict required fields and additionalProperties=false reduce ambiguous model output before deterministic validation.
    private static readonly JsonElement OutputSchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "owner_name": { "type": ["string", "null"] },
                  "due_date_text": { "type": ["string", "null"] },
                  "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
                  "source_quote": { "type": "string" },
                  "timestamp": { "type": ["string", "null"] },
                  "is_commitment": { "type": "boolean" },
                  "needs_input": {
                    "type": "array",
                    "items": { "type": "string", "enum": ["owner", "due date"] }
                  }
                },
                "required": [
                  "title", "owner_name", "due_date_text", "confidence",
                  "source_quote", "timestamp", "is_commitment", "needs_input"
                ],
                "additionalProperties": false
              }
            },
            "decisions": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["items", "decisions"],
          "additionalProperties": false
        }
        """);

    /// <summary>Reads the prompt resource once during type initialization and fails clearly if packaging is broken.</summary>
    private static string LoadSystemPrompt()
    {
        using var resource = typeof(OllamaExtractor).Assembly.GetManifestResourceStream(PromptResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded Ollama prompt resource '{PromptResourceName}' was not found.");
        using var reader = new StreamReader(resource, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().TrimEnd('\r', '\n');
    }

    /// <summary>Analyzes transcript chunks and returns de-duplicated action candidates.</summary>
    public async Task<AnalysisResult> ExtractAsync(
        string transcript,
        DateTime meetingDate,
        IReadOnlyList<string> attendees,
        CancellationToken cancellationToken = default,
        string meetingTitle = "")
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(attendees);
        cancellationToken.ThrowIfCancellationRequested();

        var settings = options.Value;
        var chunks = SplitTranscript(
            transcript,
            meetingTitle,
            meetingDate,
            attendees,
            settings);
        var stopwatch = Stopwatch.StartNew();
        var extracted = new List<ActionItemCandidate>();
        var decisions = new List<string>();
        var warnings = new List<string>();
        for (var index = 0; index < chunks.Count; index++)
        {
            var response = await ExtractChunkAsync(
                chunks[index],
                meetingTitle,
                meetingDate,
                attendees,
                settings,
                index,
                cancellationToken);

            decisions.AddRange(response.Output.Decisions);
            warnings.AddRange(response.Warnings);
            extracted.AddRange(response.Output.Items.Select(item =>
                new ActionItemCandidate(ToActionItem(item), item.IsCommitment)));
        }

        stopwatch.Stop();
        return ActionItemPostProcessor.Process(
            extracted,
            transcript,
            meetingDate,
            attendees,
            decisions,
            provider: "Ollama",
            model: settings.Model,
            elapsedMilliseconds: stopwatch.ElapsedMilliseconds,
            chunkCount: chunks.Count,
            warnings: warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Sends one bounded chunk request and retries exactly once on invalid structured output.</summary>
    private async Task<ParsedChunk> ExtractChunkAsync(
        string transcriptChunk,
        string meetingTitle,
        DateTime meetingDate,
        IReadOnlyList<string> attendees,
        OllamaOptions settings,
        int chunkIndex,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var userPrompt = BuildUserPrompt(meetingTitle, meetingDate, attendees, transcriptChunk);
                if (attempt > 0)
                {
                    userPrompt += "\n\nYour previous answer was not valid, return only JSON.";
                }

                var request = new
                {
                    model = settings.Model,
                    stream = false,
                    keep_alive = settings.KeepAlive,
                    // Set num_ctx explicitly because Ollama's smaller default can silently truncate longer transcript chunks.
                    options = new { temperature = settings.Temperature, num_ctx = settings.NumCtx },
                    format = OutputSchema,
                    messages = new[]
                    {
                        new { role = "system", content = SystemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                };
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
                using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeout.Token);
                using var client = httpClientFactory.CreateClient(OllamaOptions.HttpClientName);
                using var response = await client.PostAsJsonAsync(
                    "api/chat", request, JsonOptions, linkedToken.Token);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new LlmUnavailableException(
                        $"Model '{settings.Model}' is not installed. Run: ollama pull {settings.Model}");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new LlmUnavailableException(
                        $"Ollama returned HTTP {(int)response.StatusCode} while analyzing the transcript.");
                }

                var responseText = await response.Content.ReadAsStringAsync(linkedToken.Token);
                var parsed = ParseOutput(responseText);
                var warnings = new List<string>();
                if (parsed.PromptEvaluationCount is int promptTokenCount
                    && promptTokenCount >= Math.Ceiling(settings.NumCtx * ContextWarningThreshold))
                {
                    logger.LogWarning(
                        "Ollama used {PromptTokenCount} prompt tokens near NumCtx {NumCtx}; transcript content may have been truncated.",
                        promptTokenCount,
                        settings.NumCtx);
                    warnings.Add(
                        $"Ollama prompt used {promptTokenCount} tokens near the configured NumCtx {settings.NumCtx}; transcript content may have been truncated.");
                }

                return new ParsedChunk(parsed.Output, warnings);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                throw new LlmUnavailableException(
                    $"Ollama request timed out after {settings.RequestTimeoutSeconds} seconds.",
                    exception);
            }
            catch (HttpRequestException exception)
            {
                throw new LlmUnavailableException(
                    $"Cannot reach Ollama at {settings.BaseUrl}. Is it running?",
                    exception);
            }
            catch (JsonException) when (attempt == 0)
            {
                // Retry only once because structured output can occasionally be malformed despite the schema.
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"Ollama returned invalid structured output for transcript chunk {chunkIndex + 1} after one retry.",
                    exception);
            }
        }

        throw new InvalidOperationException("Ollama extraction ended without a response.");
    }

    /// <summary>Builds the user content while keeping the original transcript wording intact.</summary>
    private static string BuildUserPrompt(
        string meetingTitle,
        DateTime meetingDate,
        IReadOnlyList<string> attendees,
        string transcriptChunk)
    {
        var attendeeJson = JsonSerializer.Serialize(attendees, JsonOptions);
        return $"""
            Meeting title: {meetingTitle}
            Meeting date: {meetingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}
            Attendees: {attendeeJson}

            Transcript chunk:
            {transcriptChunk}
            """;
    }

    /// <summary>Parses and validates the Ollama envelope and every required schema field.</summary>
    private static ParsedResponse ParseOutput(string responseText)
    {
        using var envelope = JsonDocument.Parse(responseText);
        var responseRoot = envelope.RootElement;
        if (responseRoot.ValueKind != JsonValueKind.Object
            || !responseRoot.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Ollama response did not contain message.content.");
        }

        var structuredJson = content.GetString();
        if (string.IsNullOrWhiteSpace(structuredJson))
        {
            throw new JsonException("Ollama response content was empty.");
        }

        using var structured = JsonDocument.Parse(structuredJson);
        var root = structured.RootElement;
        // Validate locally as well as requesting constrained output because model/server versions may ignore the schema.
        ValidateProperties(root, "items", "decisions");
        var itemsElement = GetRequiredProperty(root, "items", JsonValueKind.Array);
        var decisionsElement = GetRequiredProperty(root, "decisions", JsonValueKind.Array);
        if (decisionsElement.EnumerateArray().Any(decision => decision.ValueKind != JsonValueKind.String))
        {
            throw new JsonException("Each decision must be a string.");
        }

        foreach (var item in itemsElement.EnumerateArray())
        {
            ValidateItem(item);
        }

        var parsed = JsonSerializer.Deserialize<ParsedOutput>(structuredJson, JsonOptions);
        if (parsed is null)
        {
            throw new JsonException("Ollama returned an empty extraction object.");
        }

        int? promptEvaluationCount = responseRoot.TryGetProperty("prompt_eval_count", out var promptCount)
            && promptCount.ValueKind == JsonValueKind.Number
            && promptCount.TryGetInt32(out var count)
                ? count
                : null;
        return new ParsedResponse(parsed, promptEvaluationCount);
    }

    /// <summary>Rejects missing, malformed, or unsupported item fields before mapping candidates.</summary>
    private static void ValidateItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Each extracted item must be an object.");
        }

        ValidateProperties(
            item,
            "title",
            "owner_name",
            "due_date_text",
            "confidence",
            "source_quote",
            "timestamp",
            "is_commitment",
            "needs_input");
        RequireString(item, "title", allowEmpty: false);
        RequireNullableString(item, "owner_name");
        RequireNullableString(item, "due_date_text");
        var confidence = GetRequiredProperty(item, "confidence", JsonValueKind.Number).GetDouble();
        if (confidence is < 0 or > 1)
        {
            throw new JsonException("Item confidence must be between zero and one.");
        }

        RequireString(item, "source_quote", allowEmpty: false);
        RequireNullableString(item, "timestamp");
        _ = GetRequiredProperty(item, "is_commitment", JsonValueKind.True, JsonValueKind.False);
        var needsInput = GetRequiredProperty(item, "needs_input", JsonValueKind.Array);
        foreach (var field in needsInput.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.String
                || field.GetString() is not ("owner" or "due date"))
            {
                throw new JsonException("needs_input may contain only 'owner' or 'due date'.");
            }
        }
    }

    /// <summary>Rejects properties outside the structured-output schema.</summary>
    private static void ValidateProperties(JsonElement element, params string[] expectedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object
            || element.EnumerateObject().Any(property =>
                !expectedProperties.Contains(property.Name, StringComparer.Ordinal)))
        {
            throw new JsonException("The response contains properties outside the required schema.");
        }
    }

    /// <summary>Requires a named non-empty or optionally empty string property.</summary>
    private static void RequireString(JsonElement element, string propertyName, bool allowEmpty)
    {
        var value = GetRequiredProperty(element, propertyName, JsonValueKind.String).GetString();
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
        {
            throw new JsonException($"Property '{propertyName}' must be a non-empty string.");
        }
    }

    /// <summary>Requires a property whose JSON value is either a string or null.</summary>
    private static void RequireNullableString(JsonElement element, string propertyName)
    {
        _ = GetRequiredProperty(element, propertyName, JsonValueKind.String, JsonValueKind.Null);
    }

    /// <summary>Finds a required property and verifies that its JSON kind is allowed.</summary>
    private static JsonElement GetRequiredProperty(
        JsonElement element,
        string propertyName,
        params JsonValueKind[] allowedKinds)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || !allowedKinds.Contains(value.ValueKind))
        {
            throw new JsonException($"Required property '{propertyName}' is missing or has an invalid type.");
        }

        return value;
    }

    /// <summary>Splits speaker lines to fit both the character cap and estimated context-token budget.</summary>
    private IReadOnlyList<string> SplitTranscript(
        string transcript,
        string meetingTitle,
        DateTime meetingDate,
        IReadOnlyList<string> attendees,
        OllamaOptions settings)
    {
        var fixedPrompt = SystemPrompt
            + OutputSchema.GetRawText()
            + BuildUserPrompt(meetingTitle, meetingDate, attendees, string.Empty);
        var lines = transcript.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        var current = new List<string>();

        bool UsesDevanagariEstimator(string text) =>
            ContainsDevanagari(text)
            || ContainsDevanagari(meetingTitle)
            || attendees.Any(ContainsDevanagari);

        bool Fits(string chunk)
        {
            var useDevanagariEstimator = UsesDevanagariEstimator(chunk);
            var promptTokens = EstimateTokenCount(fixedPrompt, useDevanagariEstimator);
            var transcriptTokens = EstimateTokenCount(chunk, useDevanagariEstimator);
            return chunk.Length <= settings.MaxTranscriptCharsPerChunk
                && promptTokens + transcriptTokens + ReservedOutputTokens <= settings.NumCtx;
        }

        int MaximumFragmentLength(string line)
        {
            var useDevanagariEstimator = UsesDevanagariEstimator(line);
            var promptTokens = EstimateTokenCount(fixedPrompt, useDevanagariEstimator);
            var availableTranscriptTokens = settings.NumCtx - promptTokens - ReservedOutputTokens;
            if (availableTranscriptTokens <= 0)
            {
                throw new InvalidOperationException(
                    $"The fixed Ollama prompt and {ReservedOutputTokens}-token response reserve exceed NumCtx {settings.NumCtx}.");
            }

            var charactersPerToken = useDevanagariEstimator
                ? DevanagariCharactersPerToken
                : LatinCharactersPerToken;
            var tokenCharacterLimit = (int)Math.Floor(availableTranscriptTokens * charactersPerToken);
            return Math.Min(settings.MaxTranscriptCharsPerChunk, tokenCharacterLimit);
        }

        if (!Fits(string.Empty))
        {
            throw new InvalidOperationException(
                $"The fixed Ollama prompt and {ReservedOutputTokens}-token response reserve exceed NumCtx {settings.NumCtx}.");
        }

        void FlushCurrent()
        {
            if (current.Count == 0)
            {
                return;
            }

            chunks.Add(string.Join(Environment.NewLine, current));
            current.Clear();
        }

        foreach (var line in lines)
        {
            if (!Fits(line))
            {
                FlushCurrent();
                var fragmentLength = MaximumFragmentLength(line);
                var estimatedTokens = EstimateTokenCount(line, UsesDevanagariEstimator(line));
                logger.LogWarning(
                    "A transcript line exceeds the safe Ollama chunk budget and will be fragmented. LineCharacters={LineCharacters}, EstimatedTranscriptTokens={EstimatedTranscriptTokens}, ChunkCharacterLimit={ChunkCharacterLimit}, NumCtx={NumCtx}.",
                    line.Length,
                    estimatedTokens,
                    settings.MaxTranscriptCharsPerChunk,
                    settings.NumCtx);

                for (var offset = 0; offset < line.Length;)
                {
                    var end = FindSafeFragmentEnd(line, offset, fragmentLength);
                    chunks.Add(line.Substring(offset, end - offset));
                    offset = end;
                }

                continue;
            }

            var candidate = string.Join(Environment.NewLine, current.Append(line));
            if (!Fits(candidate))
            {
                var overlap = current.TakeLast(OverlapLineCount).ToList();
                FlushCurrent();
                current = overlap;
                while (current.Count > 0 && !Fits(string.Join(Environment.NewLine, current.Append(line))))
                {
                    current.RemoveAt(0);
                }
            }

            current.Add(line);
        }

        FlushCurrent();
        return chunks.Count == 0 ? [string.Empty] : chunks;
    }

    /// <summary>Estimates token use with a conservative script-specific characters-per-token ratio.</summary>
    private static int EstimateTokenCount(string text, bool useDevanagariEstimator)
    {
        var characterCount = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            characterCount++;
        }

        var charactersPerToken = useDevanagariEstimator
            ? DevanagariCharactersPerToken
            : LatinCharactersPerToken;
        return (int)Math.Ceiling(characterCount / charactersPerToken);
    }

    /// <summary>Detects Devanagari code points to select the conservative chunk estimator.</summary>
    private static bool ContainsDevanagari(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is >= 0x0900 and <= 0x097F)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Moves a fragment boundary so it does not split a Unicode scalar or common Devanagari cluster.</summary>
    private static int FindSafeFragmentEnd(string line, int start, int fragmentLength)
    {
        if (fragmentLength <= 0)
        {
            throw new InvalidOperationException("The configured Ollama context leaves no transcript characters available.");
        }

        var end = Math.Min(start + fragmentLength, line.Length);
        if (end < line.Length && char.IsHighSurrogate(line[end - 1]) && char.IsLowSurrogate(line[end]))
        {
            end--;
        }

        while (end > start
            && end < line.Length
            && CharUnicodeInfo.GetUnicodeCategory(line, end) is
                UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
        {
            end--;
        }

        if (end > start && end < line.Length && line[end - 1] == '\u094D')
        {
            end = Math.Max(start, end - 2);
        }

        return end > start ? end : Math.Min(start + fragmentLength, line.Length);
    }

    /// <summary>Calculates a prospective joined chunk length including line separators.</summary>
    private static int JoinedLength(IReadOnlyCollection<string> lines, string nextLine) =>
        lines.Sum(line => line.Length) + Math.Max(0, lines.Count - 1) * Environment.NewLine.Length
        + (lines.Count == 0 ? 0 : Environment.NewLine.Length) + nextLine.Length;

    /// <summary>Maps the validated model proposal to the existing draft action entity shape.</summary>
    private static ActionItem ToActionItem(ParsedItem item) =>
        new()
        {
            Title = item.Title,
            OwnerName = item.OwnerName,
            DueDateText = item.DueDateText ?? string.Empty,
            Confidence = item.Confidence,
            SourceQuote = item.SourceQuote,
            Timestamp = item.Timestamp ?? string.Empty,
            NeedsInput = item.NeedsInput
        };

    /// <summary>Represents validated items and decisions returned inside chat content.</summary>
    private sealed class ParsedOutput
    {
        /// <summary>Gets the proposed action items.</summary>
        [JsonPropertyName("items")]
        public List<ParsedItem> Items { get; init; } = [];

        /// <summary>Gets the concise meeting decisions.</summary>
        [JsonPropertyName("decisions")]
        public List<string> Decisions { get; init; } = [];
    }

    /// <summary>Pairs parsed structured content with Ollama's prompt-token telemetry.</summary>
    private sealed record ParsedResponse(ParsedOutput Output, int? PromptEvaluationCount);

    /// <summary>Contains one parsed chunk and its non-fatal context-budget warnings.</summary>
    private sealed record ParsedChunk(ParsedOutput Output, IReadOnlyList<string> Warnings);

    /// <summary>Represents one schema-validated action proposal.</summary>
    private sealed class ParsedItem
    {
        /// <summary>Gets the action title.</summary>
        [JsonPropertyName("title")]
        public string Title { get; init; } = string.Empty;

        /// <summary>Gets the proposed attendee owner, if any.</summary>
        [JsonPropertyName("owner_name")]
        public string? OwnerName { get; init; }

        /// <summary>Gets the original due-date phrase, if any.</summary>
        [JsonPropertyName("due_date_text")]
        public string? DueDateText { get; init; }

        /// <summary>Gets the model's confidence value.</summary>
        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }

        /// <summary>Gets the evidence quote copied from the transcript.</summary>
        [JsonPropertyName("source_quote")]
        public string SourceQuote { get; init; } = string.Empty;

        /// <summary>Gets the transcript timestamp when present.</summary>
        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; init; }

        /// <summary>Gets whether the proposal is a real commitment.</summary>
        [JsonPropertyName("is_commitment")]
        public bool IsCommitment { get; init; }

        /// <summary>Gets fields that need organizer input.</summary>
        [JsonPropertyName("needs_input")]
        public List<string> NeedsInput { get; init; } = [];
    }
}
