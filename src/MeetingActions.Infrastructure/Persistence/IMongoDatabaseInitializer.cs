namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Initializes the configured MongoDB database before the API accepts requests.</summary>
public interface IMongoDatabaseInitializer
{
    /// <summary>Creates required indexes idempotently.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
