using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SetupProfileSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "setup_profile_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ArtifactContentHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    ArtifactLength = table.Column<long>(type: "INTEGER", nullable: false),
                    ArtifactMediaType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ArtifactCreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_profile_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_setup_profile_snapshots_artifacts_ArtifactContentHash",
                        column: x => x.ArtifactContentHash,
                        principalTable: "artifacts",
                        principalColumn: "ContentHash",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_setup_profile_snapshots_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_setup_profile_snapshots_ArtifactContentHash",
                table: "setup_profile_snapshots",
                column: "ArtifactContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_setup_profile_snapshots_InstallationId_ProfileVersion_Kind",
                table: "setup_profile_snapshots",
                columns: new[] { "InstallationId", "ProfileVersion", "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "setup_profile_snapshots");
        }
    }
}
