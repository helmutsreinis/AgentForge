using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "schedule_snapshots",
                columns: table => new
                {
                    ScheduleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NextScheduledAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    NextDueAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    PreviousSnapshotHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    SnapshotHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CausationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_snapshots", x => new { x.ScheduleId, x.Version });
                    table.ForeignKey(
                        name: "FK_schedule_snapshots_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_schedule_snapshots_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_schedule_snapshots_AgentId",
                table: "schedule_snapshots",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_schedule_snapshots_InstallationId_IdempotencyKey_Version",
                table: "schedule_snapshots",
                columns: new[] { "InstallationId", "IdempotencyKey", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_schedule_snapshots_State_NextDueAtUtcTicks",
                table: "schedule_snapshots",
                columns: new[] { "State", "NextDueAtUtcTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schedule_snapshots");
        }
    }
}
