using System.Text.Json;
using MeetingActions.Core.Abstractions;
using MeetingActions.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeetingActions.Eval;

/// <summary>Evaluates the configured transcript extractor against synthetic labeled samples.</summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Loads the labeled corpus, averages configured runs, and writes summary metrics.</summary>
    private static async Task<int> Main(string[] args)
    {
        var repositoryRoot = FindRepositoryRoot();
        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(repositoryRoot)
            .AddJsonFile("src/MeetingActions.Api/appsettings.json", optional: false)
            .AddEnvironmentVariables();
        if (string.Equals(
                Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
                "Development",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                "Development",
                StringComparison.OrdinalIgnoreCase))
        {
            configurationBuilder.AddJsonFile(
                "src/MeetingActions.Api/appsettings.Development.json",
                optional: true);
        }

        var configuration = configurationBuilder.Build();
        var arguments = ParseArguments(args, configuration);
        configuration = new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = arguments.Provider,
                ["Ollama:Model"] = arguments.Model
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging(logging => logging.AddConsole())
            .AddHttpClient()
            .AddInfrastructure(configuration)
            .BuildServiceProvider();
        var extractor = services.GetRequiredService<ILlmExtractor>();
        var samplesDirectory = Path.Combine(repositoryRoot, "samples", "eval");
        var samples = Directory.GetFiles(samplesDirectory, "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(ReadSample)
            .ToArray();
        if (samples.Length != 8)
        {
            throw new InvalidOperationException(
                $"Expected exactly eight synthetic evaluation samples, but found {samples.Length}.");
        }

        var rows = new List<EvaluationRow>(samples.Length);
        foreach (var sample in samples)
        {
            var measurements = new List<EvaluationMeasurement>(arguments.Runs);
            for (var run = 0; run < arguments.Runs; run++)
            {
                var result = await extractor.ExtractAsync(
                    sample.Transcript,
                    DateTime.Parse(sample.MeetingDate, System.Globalization.CultureInfo.InvariantCulture),
                    sample.Attendees,
                    meetingTitle: sample.Id);
                measurements.Add(Evaluate(sample, result));
            }

            rows.Add(Average(sample, measurements));
        }

        var totals = EvaluationTotals.From(rows);
        var report = FormatReport(arguments.Provider, arguments.Model, arguments.Runs, rows, totals);
        Console.WriteLine(report);
        var resultFileName = $"results-{SafeFilePart(arguments.Provider)}-{SafeFilePart(arguments.Model)}.md";
        await File.WriteAllTextAsync(Path.Combine(samplesDirectory, resultFileName), report);
        await services.DisposeAsync();
        return 0;
    }

    /// <summary>Parses an optional positional provider plus named provider, model, and run-count overrides.</summary>
    private static EvaluationArguments ParseArguments(string[] args, IConfiguration configuration)
    {
        string? providerOverride = null;
        string? modelOverride = null;
        var runs = 1;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--provider" or "--model" or "--runs")
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                {
                    throw new ArgumentException($"The {argument} option requires a value.");
                }

                var value = args[index];
                if (argument == "--provider")
                {
                    providerOverride = value;
                }
                else if (argument == "--model")
                {
                    modelOverride = value;
                }
                else if (!int.TryParse(value, out runs) || runs <= 0)
                {
                    throw new ArgumentException("--runs must be a positive integer.");
                }

                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown evaluation option '{argument}'.");
            }

            if (providerOverride is not null)
            {
                throw new ArgumentException("Specify the provider either positionally or with --provider, not both.");
            }

            providerOverride = argument;
        }

        var provider = providerOverride ?? configuration["Llm:Provider"] ?? "Stub";
        var model = modelOverride
            ?? (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
                ? configuration["Ollama:Model"] ?? "llama3.1:8b"
                : "Built-in rules");
        return new EvaluationArguments(provider, model, runs);
    }

    /// <summary>Finds the repository root by locating both the solution and the evaluation corpus.</summary>
    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MeetingActions.sln"))
                    && Directory.Exists(Path.Combine(directory.FullName, "samples", "eval")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the MeetingActions.sln and samples/eval directory.");
    }

    /// <summary>Reads one sample file while rejecting malformed or incomplete evaluation labels.</summary>
    private static EvaluationSample ReadSample(string path)
    {
        var json = File.ReadAllText(path);
        var sample = JsonSerializer.Deserialize<EvaluationSample>(json, JsonOptions)
            ?? throw new InvalidDataException($"Evaluation sample '{path}' was empty.");
        if (string.IsNullOrWhiteSpace(sample.Id)
            || string.IsNullOrWhiteSpace(sample.Language)
            || string.IsNullOrWhiteSpace(sample.Transcript)
            || sample.Attendees.Count == 0
            || sample.ExpectedItems.Any(item =>
                string.IsNullOrWhiteSpace(item.Quote)
                || string.IsNullOrWhiteSpace(item.DueDateText)))
        {
            throw new InvalidDataException($"Evaluation sample '{path}' is missing required labels.");
        }

        return sample;
    }

    /// <summary>Matches one run's predictions to labels and measures field accuracy.</summary>
    private static EvaluationMeasurement Evaluate(
        EvaluationSample sample,
        MeetingActions.Core.Services.AnalysisResult result)
    {
        var expectedByQuote = sample.ExpectedItems.ToDictionary(
            item => Normalize(item.Quote),
            StringComparer.Ordinal);
        var matched = result.Items
            .Where(item => expectedByQuote.ContainsKey(Normalize(item.SourceQuote)))
            .Select(item => (Expected: expectedByQuote[Normalize(item.SourceQuote)], Actual: item))
            .ToArray();
        var truePositives = matched.Length;
        var falsePositives = result.Items.Count - truePositives;
        var falseNegatives = sample.ExpectedItems.Count - truePositives;
        var ownerMatches = matched.Count(pair => string.Equals(
            pair.Expected.Owner,
            pair.Actual.OwnerName,
            StringComparison.OrdinalIgnoreCase));
        var duePhraseMatches = matched.Count(pair => string.Equals(
            pair.Expected.DueDateText,
            pair.Actual.DueDateText,
            StringComparison.OrdinalIgnoreCase));
        return new EvaluationMeasurement(
            result.Items.Count,
            truePositives,
            falsePositives,
            falseNegatives,
            matched.Length,
            ownerMatches,
            duePhraseMatches,
            result.DroppedCount,
            result.ElapsedMilliseconds / 1000d);
    }

    /// <summary>Averages classification metrics and timings across the configured runs for one sample.</summary>
    private static EvaluationRow Average(
        EvaluationSample sample,
        IReadOnlyList<EvaluationMeasurement> measurements)
    {
        double Mean(Func<EvaluationMeasurement, int> selector) =>
            measurements.Average(measurement => selector(measurement));

        return new EvaluationRow(
            sample.Id,
            sample.Language,
            Mean(measurement => measurement.ExtractedItems),
            sample.ExpectedItems.Count,
            Mean(measurement => measurement.TruePositives),
            Mean(measurement => measurement.FalsePositives),
            Mean(measurement => measurement.FalseNegatives),
            Mean(measurement => measurement.Matched),
            Mean(measurement => measurement.OwnerMatches),
            Mean(measurement => measurement.DuePhraseMatches),
            measurements.Average(measurement => measurement.DroppedHallucinations),
            measurements.Average(measurement => measurement.Seconds),
            measurements.Min(measurement => measurement.Seconds),
            measurements.Max(measurement => measurement.Seconds));
    }

    /// <summary>Formats transcript, corpus, and per-language metrics as a compact Markdown report.</summary>
    private static string FormatReport(
        string provider,
        string model,
        int runs,
        IReadOnlyList<EvaluationRow> rows,
        EvaluationTotals totals)
    {
        var lines = new List<string>
        {
            "# Transcript extraction evaluation",
            "",
            $"Provider: **{provider}**",
            $"Model: **{model}**",
            $"Runs per transcript: **{runs}**",
            "",
            "| Transcript | Language | Extracted | Expected | Precision | Recall | Owner accuracy | Due phrase accuracy | Dropped hallucinations | Avg (s) | Min (s) | Max (s) |",
            "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"
        };
        lines.AddRange(rows.Select(row =>
            $"| {row.Id} | {row.Language} | {Count(row.ExtractedItems)} | {row.ExpectedItems} | "
            + $"{Percent(row.TruePositives, row.TruePositives + row.FalsePositives)} | "
            + $"{Percent(row.TruePositives, row.TruePositives + row.FalseNegatives)} | "
            + $"{Percent(row.OwnerMatches, row.Matched)} | "
            + $"{Percent(row.DuePhraseMatches, row.Matched)} | {Count(row.DroppedHallucinations)} | "
            + $"{row.AverageSeconds:0.00} | {row.MinSeconds:0.00} | {row.MaxSeconds:0.00} |"));
        lines.Add(
            $"| **Total** |  | **{Count(totals.ExtractedItems)}** | **{totals.ExpectedItems}** | "
            + $"**{Percent(totals.TruePositives, totals.TruePositives + totals.FalsePositives)}** | "
            + $"**{Percent(totals.TruePositives, totals.TruePositives + totals.FalseNegatives)}** | "
            + $"**{Percent(totals.OwnerMatches, totals.Matched)}** | "
            + $"**{Percent(totals.DuePhraseMatches, totals.Matched)}** | "
            + $"**{Count(totals.DroppedHallucinations)}** | **{totals.AverageSeconds:0.00}** | "
            + $"**{totals.MinSeconds:0.00}** | **{totals.MaxSeconds:0.00}** |");
        lines.Add(string.Empty);
        lines.Add("## Per-language breakdown");
        lines.Add(string.Empty);
        lines.Add("| Language | Extracted | Expected | Precision | Recall | Owner accuracy | Due phrase accuracy | Dropped hallucinations | Avg (s) | Min (s) | Max (s) |");
        lines.Add("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var group in rows.GroupBy(row => row.Language).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var languageTotals = EvaluationTotals.From(group.ToArray());
            lines.Add(
                $"| {group.Key} | {Count(languageTotals.ExtractedItems)} | {languageTotals.ExpectedItems} | "
                + $"{Percent(languageTotals.TruePositives, languageTotals.TruePositives + languageTotals.FalsePositives)} | "
                + $"{Percent(languageTotals.TruePositives, languageTotals.TruePositives + languageTotals.FalseNegatives)} | "
                + $"{Percent(languageTotals.OwnerMatches, languageTotals.Matched)} | "
                + $"{Percent(languageTotals.DuePhraseMatches, languageTotals.Matched)} | "
                + $"{Count(languageTotals.DroppedHallucinations)} | {languageTotals.AverageSeconds:0.00} | "
                + $"{languageTotals.MinSeconds:0.00} | {languageTotals.MaxSeconds:0.00} |");
        }

        lines.Add(string.Empty);
        lines.Add("Precision/recall use exact normalized source-quote matches; field accuracies are measured over those matches.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Normalizes whitespace and casing for stable quote matching.</summary>
    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

    /// <summary>Formats a ratio as N/A when its denominator is empty.</summary>
    private static string Percent(double numerator, double denominator) =>
        denominator == 0 ? "N/A" : $"{numerator * 100d / denominator:0.0}%";

    /// <summary>Formats averaged item counts without displaying unnecessary decimal places.</summary>
    private static string Count(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Replaces filename-invalid characters so provider and model tags are safe on Windows.</summary>
    private static string SafeFilePart(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(value.Select(character =>
            invalidCharacters.Contains(character) || char.IsControl(character) || char.IsWhiteSpace(character)
                ? '-'
                : character));
    }
}

/// <summary>Stores the selected analysis provider, model override, and number of evaluation repetitions.</summary>
internal sealed record EvaluationArguments(string Provider, string Model, int Runs);

/// <summary>Describes one synthetic transcript and its manually labeled action items.</summary>
internal sealed record EvaluationSample(
    string Id,
    string Language,
    string MeetingDate,
    IReadOnlyList<string> Attendees,
    string Transcript,
    IReadOnlyList<ExpectedAction> ExpectedItems);

/// <summary>Stores an expected owner, due-date phrase, and verbatim source quote.</summary>
internal sealed record ExpectedAction(string? Owner, string DueDateText, string Quote);

/// <summary>Stores one run's prediction counts and measured elapsed time for a transcript.</summary>
internal sealed record EvaluationMeasurement(
    int ExtractedItems,
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    int Matched,
    int OwnerMatches,
    int DuePhraseMatches,
    int DroppedHallucinations,
    double Seconds);

/// <summary>Stores per-transcript counts averaged across runs and their timing range.</summary>
internal sealed record EvaluationRow(
    string Id,
    string Language,
    double ExtractedItems,
    int ExpectedItems,
    double TruePositives,
    double FalsePositives,
    double FalseNegatives,
    double Matched,
    double OwnerMatches,
    double DuePhraseMatches,
    double DroppedHallucinations,
    double AverageSeconds,
    double MinSeconds,
    double MaxSeconds);

/// <summary>Aggregates classification and field-accuracy metrics over the evaluation corpus.</summary>
internal sealed record EvaluationTotals(
    double ExtractedItems,
    int ExpectedItems,
    double TruePositives,
    double FalsePositives,
    double FalseNegatives,
    double Matched,
    double OwnerMatches,
    double DuePhraseMatches,
    double DroppedHallucinations,
    double AverageSeconds,
    double MinSeconds,
    double MaxSeconds)
{
    /// <summary>Combines per-sample values and calculates mean analysis duration.</summary>
    internal static EvaluationTotals From(IReadOnlyList<EvaluationRow> rows) =>
        new(
            rows.Sum(row => row.ExtractedItems),
            rows.Sum(row => row.ExpectedItems),
            rows.Sum(row => row.TruePositives),
            rows.Sum(row => row.FalsePositives),
            rows.Sum(row => row.FalseNegatives),
            rows.Sum(row => row.Matched),
            rows.Sum(row => row.OwnerMatches),
            rows.Sum(row => row.DuePhraseMatches),
            rows.Sum(row => row.DroppedHallucinations),
            rows.Count == 0 ? 0 : rows.Average(row => row.AverageSeconds),
            rows.Count == 0 ? 0 : rows.Min(row => row.MinSeconds),
            rows.Count == 0 ? 0 : rows.Max(row => row.MaxSeconds));
}
