using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableOrchestration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "orchestration_task_snapshots",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
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
                    table.PrimaryKey("PK_orchestration_task_snapshots", x => new { x.TaskId, x.Version });
                    table.ForeignKey(
                        name: "FK_orchestration_task_snapshots_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orchestration_task_snapshots_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_orchestration_task_snapshots_AgentId",
                table: "orchestration_task_snapshots",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_orchestration_task_snapshots_InstallationId_AgentId_UpdatedAtUtcTicks",
                table: "orchestration_task_snapshots",
                columns: new[] { "InstallationId", "AgentId", "UpdatedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_orchestration_task_snapshots_InstallationId_IdempotencyKey_Version",
                table: "orchestration_task_snapshots",
                columns: new[] { "InstallationId", "IdempotencyKey", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "orchestration_task_snapshots");
        }
    }
}
