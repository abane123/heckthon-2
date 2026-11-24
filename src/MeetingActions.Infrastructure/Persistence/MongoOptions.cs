namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Holds the MongoDB connection configuration for the application.</summary>
public sealed class MongoOptions
{
    /// <summary>Gets the configuration section name.</summary>
    public const string SectionName = "MongoDB";

    /// <summary>Gets or sets the MongoDB connection string.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Gets or sets the database name.</summary>
    public string DatabaseName { get; set; } = string.Empty;
}
