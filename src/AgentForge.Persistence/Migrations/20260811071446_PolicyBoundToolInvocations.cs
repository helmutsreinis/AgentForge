using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PolicyBoundToolInvocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tool_invocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ToolId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ToolVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ToolDescriptorHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    CapabilityId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RiskClass = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ParametersHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    WorkspaceHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    ApprovalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CausationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ExitCode = table.Column<int>(type: "INTEGER", nullable: true),
                    StandardOutputHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                    StandardOutputLength = table.Column<int>(type: "INTEGER", nullable: false),
                    StandardErrorHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                    StandardErrorLength = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_invocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tool_invocations_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tool_invocations_capability_approvals_ApprovalId",
                        column: x => x.ApprovalId,
                        principalTable: "capability_approvals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tool_invocations_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tool_invocations_AgentId",
                table: "tool_invocations",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_tool_invocations_ApprovalId",
                table: "tool_invocations",
                column: "ApprovalId");

            migrationBuilder.CreateIndex(
                name: "IX_tool_invocations_InstallationId_AgentId_CreatedAtUtcTicks",
                table: "tool_invocations",
                columns: new[] { "InstallationId", "AgentId", "CreatedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_tool_invocations_InstallationId_IdempotencyKey",
                table: "tool_invocations",
                columns: new[] { "InstallationId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tool_invocations");
        }
    }
}
