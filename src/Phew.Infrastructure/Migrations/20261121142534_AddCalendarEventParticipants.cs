using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Phew.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarEventParticipants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OverrideParticipants",
                table: "CalendarEventExceptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CalendarEventExceptionParticipants",
                columns: table => new
                {
                    CalendarEventExceptionId = table.Column<int>(type: "INTEGER", nullable: false),
                    FamilyMemberId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEventExceptionParticipants", x => new { x.CalendarEventExceptionId, x.FamilyMemberId });
                    table.ForeignKey(
                        name: "FK_CalendarEventExceptionParticipants_CalendarEventExceptions_CalendarEventExceptionId",
                        column: x => x.CalendarEventExceptionId,
                        principalTable: "CalendarEventExceptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarEventExceptionParticipants_FamilyMembers_FamilyMemberId",
                        column: x => x.FamilyMemberId,
                        principalTable: "FamilyMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CalendarEventParticipants",
                columns: table => new
                {
                    CalendarEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    FamilyMemberId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarEventParticipants", x => new { x.CalendarEventId, x.FamilyMemberId });
                    table.ForeignKey(
                        name: "FK_CalendarEventParticipants_CalendarEvents_CalendarEventId",
                        column: x => x.CalendarEventId,
                        principalTable: "CalendarEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarEventParticipants_FamilyMembers_FamilyMemberId",
                        column: x => x.FamilyMemberId,
                        principalTable: "FamilyMembers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventExceptionParticipants_FamilyMemberId",
                table: "CalendarEventExceptionParticipants",
                column: "FamilyMemberId");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEventParticipants_FamilyMemberId",
                table: "CalendarEventParticipants",
                column: "FamilyMemberId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendarEventExceptionParticipants");

            migrationBuilder.DropTable(
                name: "CalendarEventParticipants");

            migrationBuilder.DropColumn(
                name: "OverrideParticipants",
                table: "CalendarEventExceptions");
        }
    }
}
