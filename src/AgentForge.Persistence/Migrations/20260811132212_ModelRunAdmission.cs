using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModelRunAdmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    ProviderProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsFallback = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiredCapabilitiesJson = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    SelectionEvidenceHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    PlanEvidenceHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    PreparedInputHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    HealthEvidenceHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    ContextRedactionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ContextPreparationPolicy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AdmissionRequestHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    ReservedInputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    ReservedOutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    ReservedToolCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    ReservedWallClockSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    UsedInputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedOutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedToolCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    Cost = table.Column<decimal>(type: "TEXT", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CausationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FinishReason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_model_runs_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_model_runs_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_model_runs_provider_profiles_ProviderProfileId",
                        column: x => x.ProviderProfileId,
                        principalTable: "provider_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "model_run_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    ProviderType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsFallback = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiredCapabilitiesJson = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    SelectionEvidenceHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    PlanEvidenceHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    UsedInputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedOutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedToolCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    Cost = table.Column<decimal>(type: "TEXT", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: true),
                    FinishReason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsRetryable = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_run_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_model_run_attempts_model_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "model_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_model_run_attempts_provider_profiles_ProviderProfileId",
                        column: x => x.ProviderProfileId,
                        principalTable: "provider_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_run_attempts_ProviderProfileId",
                table: "model_run_attempts",
                column: "ProviderProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_model_run_attempts_RunId_Sequence",
                table: "model_run_attempts",
                columns: new[] { "RunId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_runs_AgentId",
                table: "model_runs",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_model_runs_InstallationId_AgentId_CreatedAtUtcTicks",
                table: "model_runs",
                columns: new[] { "InstallationId", "AgentId", "CreatedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_model_runs_InstallationId_IdempotencyKey",
                table: "model_runs",
                columns: new[] { "InstallationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_model_runs_ProviderProfileId",
                table: "model_runs",
                column: "ProviderProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_run_attempts");

            migrationBuilder.DropTable(
                name: "model_runs");
        }
    }
}
