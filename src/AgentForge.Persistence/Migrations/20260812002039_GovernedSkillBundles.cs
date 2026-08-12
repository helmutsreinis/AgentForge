using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GovernedSkillBundles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skill_bundle_proposal_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BundleId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BundleVersion = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DefinitionHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    PreviousSnapshotHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    SnapshotHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_bundle_proposal_snapshots", x => new { x.Id, x.Version });
                    table.ForeignKey(
                        name: "FK_skill_bundle_proposal_snapshots_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_skill_bundle_proposal_snapshots_InstallationId_BundleId_UpdatedAtUtcTicks",
                table: "skill_bundle_proposal_snapshots",
                columns: new[] { "InstallationId", "BundleId", "UpdatedAtUtcTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "skill_bundle_proposal_snapshots");
        }
    }
}
