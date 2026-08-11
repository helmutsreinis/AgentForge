using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModelRunExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttemptedProfileIdsJson",
                table: "model_runs",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "EventCount",
                table: "model_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EventStreamHash",
                table: "model_runs",
                type: "TEXT",
                maxLength: 71,
                nullable: false,
                defaultValue: "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

            migrationBuilder.AddColumn<long>(
                name: "LastEventSequence",
                table: "model_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: -1L);

            migrationBuilder.AddColumn<long>(
                name: "LeaseAcquiredAtUtcTicks",
                table: "model_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LeaseExpiresAtUtcTicks",
                table: "model_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LeaseHeartbeatAtUtcTicks",
                table: "model_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "model_runs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseTokenHash",
                table: "model_runs",
                type: "TEXT",
                maxLength: 71,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReservedEvents",
                table: "model_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "EventCount",
                table: "model_run_attempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EventStreamHash",
                table: "model_run_attempts",
                type: "TEXT",
                maxLength: 71,
                nullable: false,
                defaultValue: "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");

            migrationBuilder.AddColumn<long>(
                name: "LastEventSequence",
                table: "model_run_attempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: -1L);

            migrationBuilder.CreateTable(
                name: "model_budget_ledgers",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    ReservedInputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    ReservedOutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    ReservedToolCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    ReservedEvents = table.Column<int>(type: "INTEGER", nullable: false),
                    ReservedWallClockSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ActiveRuns = table.Column<int>(type: "INTEGER", nullable: false),
                    ConsumedInputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsumedOutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsumedToolCalls = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsumedEvents = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsumedWallClockSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedRuns = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_budget_ledgers", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_model_budget_ledgers_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_model_budget_ledgers_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_budget_ledgers_InstallationId_AgentId",
                table: "model_budget_ledgers",
                columns: new[] { "InstallationId", "AgentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_budget_ledgers");

            migrationBuilder.DropColumn(
                name: "AttemptedProfileIdsJson",
                table: "model_runs");

            migrationBuilder.DropColumn(
                name: "EventCount",
                table: "model_runs");

            migrationBuilder.DropColumn(
                name: "EventStreamHash",
                table: "model_runs");

            migrationBuilder.DropColumn(
                name: "LastEventSequence",
                table: "model_runs");

            migrationBuilder.DropColumn(
                name: "LeaseAcquiredAtUtcTicks",
                table: "model_runs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUtcTicks",
                table: "model_runs");

            migrationBuilder.DropColumn(
                name: "LeaseHeartbeatAtUtcTicks",
                table: "model_runs");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "model_runs");

            migrationBuilder.DropColumn(
                name: "LeaseTokenHash",
                table: "model_runs");

            migrationBuilder.DropColumn(
                name: "ReservedEvents",
                table: "model_runs");

            migrationBuilder.DropColumn(
                name: "EventCount",
                table: "model_run_attempts");

            migrationBuilder.DropColumn(
                name: "EventStreamHash",
                table: "model_run_attempts");

            migrationBuilder.DropColumn(
                name: "LastEventSequence",
                table: "model_run_attempts");
        }
    }
}
