using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableSerialCaptures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "serial_captures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PhysicalDeviceId = table.Column<string>(type: "TEXT", maxLength: 78, nullable: false),
                    ArtifactContentHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    StreamHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StartedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CaptureJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_serial_captures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_serial_captures_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_serial_captures_artifacts_ArtifactContentHash",
                        column: x => x.ArtifactContentHash,
                        principalTable: "artifacts",
                        principalColumn: "ContentHash",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_serial_captures_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_serial_captures_AgentId",
                table: "serial_captures",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_serial_captures_ArtifactContentHash",
                table: "serial_captures",
                column: "ArtifactContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_serial_captures_InstallationId_IdempotencyKey",
                table: "serial_captures",
                columns: new[] { "InstallationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_serial_captures_InstallationId_PhysicalDeviceId_StartedAtUtcTicks",
                table: "serial_captures",
                columns: new[] { "InstallationId", "PhysicalDeviceId", "StartedAtUtcTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "serial_captures");
        }
    }
}
