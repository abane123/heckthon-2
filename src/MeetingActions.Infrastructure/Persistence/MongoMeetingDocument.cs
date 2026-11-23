using MeetingActions.Core.Services;

namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Stores meeting metadata separately from action items while retaining their series key.</summary>
internal sealed class MongoMeetingDocument
{
    /// <summary>Gets or sets the meeting identifier stored as MongoDB's document ID.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the existing meeting title used as the current series identifier.</summary>
    public string SeriesId { get; set; } = string.Empty;

    /// <summary>Gets or sets the meeting title returned by the API.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the meeting date.</summary>
    public DateTime MeetingDate { get; set; }

    /// <summary>Gets or sets the source transcript.</summary>
    public string Transcript { get; set; } = string.Empty;

    /// <summary>Gets or sets the meeting attendees.</summary>
    public List<string> Attendees { get; set; } = [];

    /// <summary>Gets or sets decisions separated from action items during extraction.</summary>
    public List<string>? Decisions { get; set; }

    /// <summary>Gets or sets provider analysis details returned on the meeting.</summary>
    public MeetingAnalysisMetadata? Analysis { get; set; }
}
