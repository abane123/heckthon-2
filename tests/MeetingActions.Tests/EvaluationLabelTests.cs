using System.Text.Json;
using FluentAssertions;

namespace MeetingActions.Tests;

/// <summary>Protects the synthetic evaluation corpus from labels unsupported by their transcripts.</summary>
public sealed class EvaluationLabelTests
{
    /// <summary>Fails when any expected action quote is absent from its paired transcript.</summary>
    [Fact]
    public void EveryExpectedQuoteAppearsInItsTranscript()
    {
        var repositoryRoot = FindRepositoryRoot();
        var labelFiles = Directory.GetFiles(
            Path.Combine(repositoryRoot, "samples", "eval"),
            "*.json");

        labelFiles.Should().NotBeEmpty();
        foreach (var labelFile in labelFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(labelFile));
            var root = document.RootElement;
            var transcript = root.GetProperty("transcript").GetString()!;
            foreach (var expectedItem in root.GetProperty("expectedItems").EnumerateArray())
            {
                var quote = expectedItem.GetProperty("quote").GetString()!;
                transcript.Should().Contain(
                    quote,
                    $"the frozen expected quote in {Path.GetFileName(labelFile)} must occur verbatim in its transcript");
            }
        }
    }

    /// <summary>Finds the repository root from either the test working directory or output directory.</summary>
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

        throw new DirectoryNotFoundException("Could not locate MeetingActions.sln for evaluation label validation.");
    }
}
