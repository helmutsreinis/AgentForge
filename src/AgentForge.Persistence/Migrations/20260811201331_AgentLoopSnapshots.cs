using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentLoopSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_loop_snapshots",
                columns: table => new
                {
                    LoopId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Turn = table.Column<int>(type: "INTEGER", nullable: false),
                    Phase = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MaximumTurns = table.Column<int>(type: "INTEGER", nullable: false),
                    MaximumToolCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    MaximumInputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    MaximumOutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    MaximumWallClockSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaximumStructuredRepairs = table.Column<int>(type: "INTEGER", nullable: false),
                    MaximumConsecutiveNoProgress = table.Column<int>(type: "INTEGER", nullable: false),
                    UsedInputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedOutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedToolCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    UsedWallClockSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    StructuredRepairCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsecutiveNoProgress = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletionPending = table.Column<bool>(type: "INTEGER", nullable: false),
                    InitialStateHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    LastProgressEvidenceHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                    StepEvidenceHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    PreviousSnapshotHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    SnapshotHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    StartedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CausationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_loop_snapshots", x => new { x.LoopId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_agent_loop_snapshots_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_loop_snapshots_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_loop_snapshots_AgentId",
                table: "agent_loop_snapshots",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_loop_snapshots_InstallationId_AgentId_UpdatedAtUtcTicks",
                table: "agent_loop_snapshots",
                columns: new[] { "InstallationId", "AgentId", "UpdatedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_loop_snapshots_InstallationId_IdempotencyKey_Sequence",
                table: "agent_loop_snapshots",
                columns: new[] { "InstallationId", "IdempotencyKey", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_loop_snapshots");
        }
    }
}
