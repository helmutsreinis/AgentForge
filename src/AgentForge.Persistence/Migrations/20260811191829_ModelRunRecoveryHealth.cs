using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModelRunRecoveryHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_provider_health",
                columns: table => new
                {
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    EvidenceCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ObservedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    RetryAfterUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    LastRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastAttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CausationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_provider_health", x => x.ProfileId);
                    table.ForeignKey(
                        name: "FK_model_provider_health_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_model_provider_health_model_run_attempts_LastAttemptId",
                        column: x => x.LastAttemptId,
                        principalTable: "model_run_attempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_model_provider_health_model_runs_LastRunId",
                        column: x => x.LastRunId,
                        principalTable: "model_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_model_provider_health_provider_profiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "provider_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_health_InstallationId_UpdatedAtUtcTicks",
                table: "model_provider_health",
                columns: new[] { "InstallationId", "UpdatedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_health_LastAttemptId",
                table: "model_provider_health",
                column: "LastAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_model_provider_health_LastRunId",
                table: "model_provider_health",
                column: "LastRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_provider_health");
        }
    }
}
