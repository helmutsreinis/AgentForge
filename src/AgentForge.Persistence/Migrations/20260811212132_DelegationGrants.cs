using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DelegationGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "delegation_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentTaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentAgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChildAgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GrantHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    GrantJson = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IssuedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CausationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delegation_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delegation_grants_agent_identities_ChildAgentId",
                        column: x => x.ChildAgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delegation_grants_agent_identities_ParentAgentId",
                        column: x => x.ParentAgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delegation_grants_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_delegation_grants_ChildAgentId",
                table: "delegation_grants",
                column: "ChildAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_delegation_grants_InstallationId_ParentTaskId_IssuedAtUtcTicks",
                table: "delegation_grants",
                columns: new[] { "InstallationId", "ParentTaskId", "IssuedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_delegation_grants_ParentAgentId",
                table: "delegation_grants",
                column: "ParentAgentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delegation_grants");
        }
    }
}
