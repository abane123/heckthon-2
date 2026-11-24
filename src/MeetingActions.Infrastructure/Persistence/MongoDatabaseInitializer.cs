namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Creates indexes for the configured MongoDB collections.</summary>
public sealed class MongoDatabaseInitializer(MongoContext context) : IMongoDatabaseInitializer
{
    /// <summary>Creates required indexes idempotently.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        context.EnsureIndexesAsync(cancellationToken);
}
