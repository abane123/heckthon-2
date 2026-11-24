using System.Text.RegularExpressions;
using MeetingActions.Core.Abstractions;
using MeetingActions.Core.Domain;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Stores users and resolves owner names without exposing MongoDB query types to Core.</summary>
public sealed class MongoUserRepository(MongoContext context) : IUserRepository
{
    /// <summary>Gets all users ordered by display name.</summary>
    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await context.Users.Find(Builders<User>.Filter.Empty).SortBy(user => user.Name).ToListAsync(cancellationToken);

    /// <summary>Finds one user by case-insensitive display name.</summary>
    public async Task<User?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        // Escaping prevents punctuation in a display name from being interpreted as a regex expression.
        var regex = new BsonRegularExpression($"^{Regex.Escape(name)}$", "i");
        return await context.Users.Find(Builders<User>.Filter.Regex(user => user.Name, regex))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Inserts users for the demo environment.</summary>
    public Task AddManyAsync(IReadOnlyCollection<User> users, CancellationToken cancellationToken = default) =>
        users.Count == 0
            ? Task.CompletedTask
            : context.Users.InsertManyAsync(users, cancellationToken: cancellationToken);
}
