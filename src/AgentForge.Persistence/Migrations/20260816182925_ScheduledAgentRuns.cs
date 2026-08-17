using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledAgentRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scheduled_agent_run_templates",
                columns: table => new
                {
                    ScheduleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SystemInstructionArtifactHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    PromptArtifactHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    TemplateHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    TemplateJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_agent_run_templates", x => x.ScheduleId);
                    table.ForeignKey(
                        name: "FK_scheduled_agent_run_templates_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_scheduled_agent_run_templates_artifacts_PromptArtifactHash",
                        column: x => x.PromptArtifactHash,
                        principalTable: "artifacts",
                        principalColumn: "ContentHash",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_scheduled_agent_run_templates_artifacts_SystemInstructionArtifactHash",
                        column: x => x.SystemInstructionArtifactHash,
                        principalTable: "artifacts",
                        principalColumn: "ContentHash",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_scheduled_agent_run_templates_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_scheduled_agent_run_templates_provider_profiles_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "provider_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_agent_run_templates_AgentId",
                table: "scheduled_agent_run_templates",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_agent_run_templates_InstallationId_CreatedAtUtcTicks",
                table: "scheduled_agent_run_templates",
                columns: new[] { "InstallationId", "CreatedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_agent_run_templates_PromptArtifactHash",
                table: "scheduled_agent_run_templates",
                column: "PromptArtifactHash");

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_agent_run_templates_ProviderId",
                table: "scheduled_agent_run_templates",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_agent_run_templates_SystemInstructionArtifactHash",
                table: "scheduled_agent_run_templates",
                column: "SystemInstructionArtifactHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scheduled_agent_run_templates");
        }
    }
}
