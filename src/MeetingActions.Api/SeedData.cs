using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Core.Services;

namespace MeetingActions.Api;

/// <summary>Builds and stores the standard local demo data through persistence abstractions.</summary>
public static class SeedData
{
    /// <summary>Seeds users and one sample meeting when the configured store is empty.</summary>
    public static async Task InitializeAsync(
        IDemoDataStore demoDataStore,
        ILlmExtractor extractor,
        IClock clock,
        CancellationToken cancellationToken = default)
    {
        if (!await demoDataStore.IsEmptyAsync(cancellationToken))
        {
            return;
        }

        var users = CreateSampleUsers();
        var transcriptPath = Path.Combine(AppContext.BaseDirectory, "meeting-transcript.txt");
        var transcript = await File.ReadAllTextAsync(transcriptPath, cancellationToken);
        var attendees = users.Select(user => user.Name).ToArray();
        var meeting = new Meeting
        {
            Title = "Release Readiness",
            MeetingDate = clock.UtcNow.AddDays(-1),
            Transcript = transcript,
            Attendees = attendees.ToList()
        };
        var analysis = await extractor.ExtractAsync(
            transcript, meeting.MeetingDate, attendees, cancellationToken, meeting.Title);
        meeting.Items = analysis.Items.ToList();
        meeting.Decisions = analysis.Decisions.ToList();
        meeting.Analysis = new MeetingAnalysisMetadata(
            analysis.Provider,
            analysis.Model,
            analysis.ElapsedMilliseconds,
            analysis.DroppedCount,
            analysis.ChunkCount);
        AssignOwners(meeting, users);
        await demoDataStore.SeedAsync(users, meeting, cancellationToken);
    }

    /// <summary>Resets the virtual clock and replaces stored data with the deadline demo scenario.</summary>
    public static async Task ResetDemoAsync(
        IDemoDataStore demoDataStore,
        VirtualClock clock,
        CancellationToken cancellationToken = default)
    {
        clock.ResetTo(DateTime.UtcNow.Date);
        var users = CreateResetUsers();
        var meeting = CreateResetMeeting(clock.UtcNow.Date, users);
        await demoDataStore.ResetAsync(users, meeting, cancellationToken);
    }

    /// <summary>Creates the five users used by the initial sample meeting.</summary>
    private static User[] CreateSampleUsers() =>
    [
        new User { Name = "Priya", Email = "priya@example.invalid", ExternalUserId = "demo-priya" },
        new User { Name = "Alex", Email = "alex@example.invalid", ExternalUserId = "demo-alex" },
        new User { Name = "Jordan", Email = "jordan@example.invalid", ExternalUserId = "demo-jordan" },
        new User { Name = "Sam", Email = "sam@example.invalid", ExternalUserId = "demo-sam" },
        new User { Name = "Casey", Email = "casey@example.invalid", ExternalUserId = "demo-casey" }
    ];

    /// <summary>Creates the three attendees used by the reset demo scenario.</summary>
    private static User[] CreateResetUsers() =>
    [
        new User { Name = "Priya", Email = "priya@example.invalid", ExternalUserId = "demo-priya" },
        new User { Name = "Alex", Email = "alex@example.invalid", ExternalUserId = "demo-alex" },
        new User { Name = "Jordan", Email = "jordan@example.invalid", ExternalUserId = "demo-jordan" }
    ];

    /// <summary>Creates a meeting and five ticketed actions relative to the supplied virtual date.</summary>
    private static Meeting CreateResetMeeting(DateTime today, IReadOnlyCollection<User> users)
    {
        var meeting = new Meeting
        {
            Title = "Release Readiness Demo",
            MeetingDate = today,
            Attendees = ["Priya", "Alex", "Jordan"],
            Transcript = string.Join(Environment.NewLine,
                "Priya will review the release checklist tomorrow.",
                "Alex will verify the smoke tests today.",
                "Jordan will update the rollout notes after the deadline.",
                "Priya will close the retry policy follow-up after the deadline.",
                "Alex completed the documentation review.")
        };
        meeting.Items =
        [
            new ActionItem
            {
                Title = "Review the release checklist",
                OwnerName = "Priya",
                DueDate = today.AddDays(1),
                DueDateText = "tomorrow",
                Status = ActionStatus.Created,
                Confidence = 0.9,
                SourceQuote = "Priya will review the release checklist tomorrow.",
                Timestamp = "00:30",
                ExternalTicketId = "ADO-DEMO-001"
            },
            new ActionItem
            {
                Title = "Verify the smoke tests",
                OwnerName = "Alex",
                DueDate = today,
                DueDateText = "today",
                Status = ActionStatus.InProgress,
                Confidence = 0.9,
                SourceQuote = "Alex will verify the smoke tests today.",
                Timestamp = "02:10",
                ExternalTicketId = "ADO-DEMO-002"
            },
            new ActionItem
            {
                Title = "Update the rollout notes",
                OwnerName = "Jordan",
                DueDate = today.AddDays(-1),
                DueDateText = "yesterday",
                Status = ActionStatus.Overdue,
                Confidence = 0.85,
                SourceQuote = "Jordan will update the rollout notes after the deadline.",
                Timestamp = "04:15",
                ExternalTicketId = "ADO-DEMO-003",
                SlipCount = 1
            },
            new ActionItem
            {
                Title = "Close the retry policy follow-up",
                OwnerName = "Priya",
                DueDate = today.AddDays(-2),
                DueDateText = "two days ago",
                Status = ActionStatus.Overdue,
                Confidence = 0.85,
                SourceQuote = "Priya will close the retry policy follow-up after the deadline.",
                Timestamp = "06:20",
                ExternalTicketId = "ADO-DEMO-004",
                SlipCount = 2
            },
            new ActionItem
            {
                Title = "Complete the documentation review",
                OwnerName = "Alex",
                DueDate = today.AddDays(-1),
                DueDateText = "yesterday",
                Status = ActionStatus.Done,
                Confidence = 0.95,
                SourceQuote = "Alex completed the documentation review.",
                Timestamp = "08:40",
                ExternalTicketId = "ADO-DEMO-005"
            }
        ];
        AssignOwners(meeting, users);
        return meeting;
    }

    /// <summary>Sets meeting identifiers and maps item owner names to the supplied demo users.</summary>
    private static void AssignOwners(Meeting meeting, IReadOnlyCollection<User> users)
    {
        var usersByName = users.ToDictionary(user => user.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var item in meeting.Items)
        {
            item.MeetingId = meeting.Id;
            item.OwnerId = item.OwnerName is not null && usersByName.TryGetValue(item.OwnerName, out var owner)
                ? owner.Id
                : null;
        }
    }
}
