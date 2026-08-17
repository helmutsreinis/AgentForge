using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentContextCapacityPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_context_policies",
                columns: table => new
                {
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DiscoveredContextWindowTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    DiscoveredContextModel = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ContextWindowOverrideTokens = table.Column<long>(type: "INTEGER", nullable: true),
                    ContextCompressionEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ContextCompressionThresholdPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    ContextCompressionTargetPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    ContextProtectedRecentTurns = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_context_policies", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_agent_context_policies_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_context_policies");
        }
    }
}
