using MeetingActions.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MeetingActions.Infrastructure.Migrations;

/// <summary>Creates the initial SQLite schema for the meeting-action pipeline.</summary>
[DbContext(typeof(MeetingActionsDbContext))]
[Migration("202611170001_InitialCreate")]
public sealed class InitialCreate : Migration
{
    /// <summary>Creates the initial tables and their meeting-item relationship.</summary>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Meetings",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                Title = table.Column<string>(nullable: false),
                MeetingDate = table.Column<DateTime>(nullable: false),
                Transcript = table.Column<string>(nullable: false),
                Attendees = table.Column<string>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Meetings", row => row.Id));
        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                Name = table.Column<string>(nullable: false),
                Email = table.Column<string>(nullable: false),
                ExternalUserId = table.Column<string>(nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_Users", row => row.Id));
        migrationBuilder.CreateTable(
            name: "ActionItems",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                MeetingId = table.Column<Guid>(nullable: false),
                Title = table.Column<string>(nullable: false),
                OwnerId = table.Column<Guid>(nullable: true),
                OwnerName = table.Column<string>(nullable: true),
                DueDate = table.Column<DateTime>(nullable: true),
                DueDateText = table.Column<string>(nullable: false),
                Status = table.Column<string>(nullable: false),
                Confidence = table.Column<double>(nullable: false),
                SourceQuote = table.Column<string>(nullable: false),
                Timestamp = table.Column<string>(nullable: false),
                NeedsInput = table.Column<string>(nullable: false),
                ExternalTicketId = table.Column<string>(nullable: true),
                SlipCount = table.Column<int>(nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ActionItems", row => row.Id);
                table.ForeignKey("FK_ActionItems_Meetings_MeetingId", row => row.MeetingId, "Meetings", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateTable(
            name: "FollowUps",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                ActionItemId = table.Column<Guid>(nullable: false),
                SentAt = table.Column<DateTime>(nullable: false),
                Channel = table.Column<string>(nullable: false),
                Response = table.Column<string>(nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_FollowUps", row => row.Id));
        migrationBuilder.CreateTable(
            name: "AuditEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(nullable: false),
                ActionItemId = table.Column<Guid>(nullable: false),
                EventType = table.Column<string>(nullable: false),
                Actor = table.Column<string>(nullable: false),
                CreatedAt = table.Column<DateTime>(nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AuditEvents", row => row.Id));
        migrationBuilder.CreateIndex("IX_ActionItems_MeetingId", "ActionItems", "MeetingId");
        migrationBuilder.CreateIndex("IX_Users_Name", "Users", "Name", unique: true);
    }

    /// <summary>Drops the initial tables in reverse dependency order.</summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ActionItems");
        migrationBuilder.DropTable("AuditEvents");
        migrationBuilder.DropTable("FollowUps");
        migrationBuilder.DropTable("Meetings");
        migrationBuilder.DropTable("Users");
    }
}
