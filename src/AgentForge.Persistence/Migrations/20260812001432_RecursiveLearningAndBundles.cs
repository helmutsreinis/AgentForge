using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecursiveLearningAndBundles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "learning_candidate_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SignalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SkillProposalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CandidatePackageHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    BaselinePackageHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                    PreviousSnapshotHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    SnapshotHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_candidate_snapshots", x => new { x.Id, x.Version });
                    table.ForeignKey(
                        name: "FK_learning_candidate_snapshots_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "learning_signals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SignalHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    ClassificationHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    CapturedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    SignalJson = table.Column<string>(type: "TEXT", nullable: false),
                    ClassificationJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_signals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_learning_signals_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "skill_bundles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DefinitionHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    SourceSignalHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_bundles", x => new { x.Id, x.Version });
                });

            migrationBuilder.CreateIndex(
                name: "IX_learning_candidate_snapshots_InstallationId_SkillId_UpdatedAtUtcTicks",
                table: "learning_candidate_snapshots",
                columns: new[] { "InstallationId", "SkillId", "UpdatedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_signals_InstallationId_CapturedAtUtcTicks",
                table: "learning_signals",
                columns: new[] { "InstallationId", "CapturedAtUtcTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "learning_candidate_snapshots");

            migrationBuilder.DropTable(
                name: "learning_signals");

            migrationBuilder.DropTable(
                name: "skill_bundles");
        }
    }
}
