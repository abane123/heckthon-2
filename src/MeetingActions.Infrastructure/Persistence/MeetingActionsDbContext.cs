using System.Text.Json;
using MeetingActions.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Stores meeting, action, user, follow-up, and audit data.</summary>
public sealed class MeetingActionsDbContext(DbContextOptions<MeetingActionsDbContext> options) : DbContext(options)
{
    private static readonly ValueComparer<List<string>> StringListComparer = new(
        (left, right) => ReferenceEquals(left, right) || (left != null && right != null && left.SequenceEqual(right)),
        value => value == null ? 0 : value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode(StringComparison.Ordinal))),
        value => value == null ? new List<string>() : value.ToList());

    /// <summary>Gets meeting records.</summary>
    public DbSet<Meeting> Meetings => Set<Meeting>();
    /// <summary>Gets action-item records.</summary>
    public DbSet<ActionItem> ActionItems => Set<ActionItem>();
    /// <summary>Gets user records.</summary>
    public DbSet<User> Users => Set<User>();
    /// <summary>Gets notification follow-up records.</summary>
    public DbSet<FollowUp> FollowUps => Set<FollowUp>();
    /// <summary>Gets audit records.</summary>
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    /// <summary>Configures entity relationships and compact JSON storage for lists.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var attendees = modelBuilder.Entity<Meeting>().Property(meeting => meeting.Attendees);
        attendees.HasConversion(value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new List<string>());
        attendees.Metadata.SetValueComparer(StringListComparer);
        var needsInput = modelBuilder.Entity<ActionItem>().Property(item => item.NeedsInput);
        needsInput.HasConversion(value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new List<string>());
        needsInput.Metadata.SetValueComparer(StringListComparer);
        modelBuilder.Entity<Meeting>().HasMany(meeting => meeting.Items).WithOne(item => item.Meeting)
            .HasForeignKey(item => item.MeetingId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ActionItem>().Property(item => item.Status).HasConversion<string>();
        modelBuilder.Entity<ActionItem>().HasIndex(item => item.MeetingId);
        modelBuilder.Entity<User>().HasIndex(user => user.Name).IsUnique();
        base.OnModelCreating(modelBuilder);
    }
}
