using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CapabilityApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "capability_approvals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CapabilityId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RiskClass = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ToolId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ToolVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ParametersHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    TargetKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    WorkspaceHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    Disposition = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PreviewHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsumedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capability_approvals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_capability_approvals_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_capability_approvals_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_capability_approvals_AgentId",
                table: "capability_approvals",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_capability_approvals_InstallationId_AgentId_RequestHash_CreatedAtUtcTicks",
                table: "capability_approvals",
                columns: new[] { "InstallationId", "AgentId", "RequestHash", "CreatedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_capability_approvals_InstallationId_IdempotencyKey",
                table: "capability_approvals",
                columns: new[] { "InstallationId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "capability_approvals");
        }
    }
}
