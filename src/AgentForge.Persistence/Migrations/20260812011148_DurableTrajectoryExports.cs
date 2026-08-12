using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableTrajectoryExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trajectory_exports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    ArtifactContentHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trajectory_exports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_trajectory_exports_artifacts_ArtifactContentHash",
                        column: x => x.ArtifactContentHash,
                        principalTable: "artifacts",
                        principalColumn: "ContentHash",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_trajectory_exports_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_trajectory_exports_ArtifactContentHash",
                table: "trajectory_exports",
                column: "ArtifactContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_trajectory_exports_InstallationId_IdempotencyKey",
                table: "trajectory_exports",
                columns: new[] { "InstallationId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trajectory_exports");
        }
    }
}
