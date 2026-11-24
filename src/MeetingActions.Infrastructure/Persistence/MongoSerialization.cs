using MeetingActions.Core.Domain;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;

namespace MeetingActions.Infrastructure.Persistence;

/// <summary>Registers MongoDB naming, date, enum, and domain-class serialization once per process.</summary>
public static class MongoSerialization
{
    private static int _registered;

    /// <summary>Registers stable BSON conventions and class maps before any collection is accessed.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        BsonSerializer.RegisterSerializer(new DateTimeSerializer(DateTimeKind.Utc));
        BsonSerializer.RegisterSerializer(new DateOnlySerializer(BsonType.DateTime));
        var conventions = new ConventionPack
        {
            new CamelCaseElementNameConvention(),
            new EnumRepresentationConvention(BsonType.String),
            new IgnoreExtraElementsConvention(true)
        };
        ConventionRegistry.Register("MeetingActionsMongoConventions", conventions, _ => true);

        RegisterEntityMap<Meeting>(map => map.UnmapMember(meeting => meeting.Items));
        RegisterEntityMap<ActionItem>(map => map.UnmapMember(item => item.Meeting));
        RegisterEntityMap<User>(_ => { });
        RegisterEntityMap<FollowUp>(_ => { });
        RegisterEntityMap<AuditEvent>(_ => { });
        RegisterEntityMap<MongoMeetingDocument>(_ => { });
    }

    /// <summary>Registers an entity map while leaving any earlier registration intact.</summary>
    private static void RegisterEntityMap<T>(Action<BsonClassMap<T>> configure) where T : class
    {
        if (BsonClassMap.IsClassMapRegistered(typeof(T)))
        {
            return;
        }

        BsonClassMap.RegisterClassMap<T>(map =>
        {
            map.AutoMap();
            map.MapIdMember(typeof(T).GetProperty("Id")!);
            configure(map);
        });
    }
}
