using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableScopedMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "memory_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScopeId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    SourceKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SourceEvidenceHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    SourceUri = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CausationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RedactionCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_memory_entries_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_memory_entries_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_memory_entries_AgentId",
                table: "memory_entries",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_entries_InstallationId_AgentId_ScopeId_Kind_ExpiresAtUtcTicks",
                table: "memory_entries",
                columns: new[] { "InstallationId", "AgentId", "ScopeId", "Kind", "ExpiresAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_entries_InstallationId_IdempotencyKey",
                table: "memory_entries",
                columns: new[] { "InstallationId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_entries");
        }
    }
}
