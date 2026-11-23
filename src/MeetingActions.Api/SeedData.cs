using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MeetingActions.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MeetingActions.Api;

/// <summary>Creates stable local demo users and one example meeting on first startup.</summary>
public static class SeedData
{
    /// <summary>Seeds users and a sample meeting when the database is empty.</summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var dbContext = services.GetRequiredService<MeetingActionsDbContext>();
        if (!await dbContext.Users.AnyAsync(cancellationToken))
        {
            dbContext.Users.AddRange(
                new User { Name = "Priya", Email = "priya@example.invalid", ExternalUserId = "demo-priya" },
                new User { Name = "Alex", Email = "alex@example.invalid", ExternalUserId = "demo-alex" },
                new User { Name = "Jordan", Email = "jordan@example.invalid", ExternalUserId = "demo-jordan" },
                new User { Name = "Sam", Email = "sam@example.invalid", ExternalUserId = "demo-sam" },
                new User { Name = "Casey", Email = "casey@example.invalid", ExternalUserId = "demo-casey" });
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (await dbContext.Meetings.AnyAsync(cancellationToken))
        {
            return;
        }

        var transcriptPath = Path.Combine(AppContext.BaseDirectory, "meeting-transcript.txt");
        var transcript = await File.ReadAllTextAsync(transcriptPath, cancellationToken);
        var attendees = new[] { "Priya", "Alex", "Jordan", "Sam", "Casey" };
        var clock = services.GetRequiredService<IClock>();
        var meeting = new Meeting
        {
            Title = "Release Readiness",
            MeetingDate = clock.UtcNow.AddDays(-1),
            Transcript = transcript,
            Attendees = attendees.ToList()
        };
        var extractor = services.GetRequiredService<ILlmExtractor>();
        meeting.Items = (await extractor.ExtractAsync(transcript, meeting.MeetingDate, attendees, cancellationToken)).ToList();
        foreach (var item in meeting.Items)
        {
            item.MeetingId = meeting.Id;
            item.OwnerId = await dbContext.Users.Where(user => user.Name == item.OwnerName)
                .Select(user => (Guid?)user.Id).FirstOrDefaultAsync(cancellationToken);
        }

        dbContext.Meetings.Add(meeting);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
