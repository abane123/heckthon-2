using MeetingActions.Core.Abstractions;
using MongoDB.Driver;

namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Checks MongoDB reachability without exposing connection details to API consumers.</summary>
public sealed class MongoPersistenceHealthCheck(MongoContext context) : IPersistenceHealthCheck
{
    /// <summary>Returns whether the configured MongoDB server can answer a ping.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await context.PingAsync(cancellationToken);
            return true;
        }
        catch (MongoException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
