using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GovernedSkills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "skill_proposal_snapshots",
                columns: table => new
                {
                    ProposalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PreviousSnapshotHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    SnapshotHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    ProposalJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_proposal_snapshots", x => new { x.ProposalId, x.Version });
                    table.ForeignKey(
                        name: "FK_skill_proposal_snapshots_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "skill_run_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SnapshotHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CausationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_run_snapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_skill_run_snapshots_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "skill_versions",
                columns: table => new
                {
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ArtifactContentHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    PackageHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    ManifestHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Provenance = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DescriptorJson = table.Column<string>(type: "TEXT", nullable: false),
                    RecordVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_versions", x => new { x.InstallationId, x.SkillId, x.Version });
                    table.ForeignKey(
                        name: "FK_skill_versions_artifacts_ArtifactContentHash",
                        column: x => x.ArtifactContentHash,
                        principalTable: "artifacts",
                        principalColumn: "ContentHash",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_skill_versions_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "skill_active_versions",
                columns: table => new
                {
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SkillId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_skill_active_versions", x => new { x.InstallationId, x.SkillId });
                    table.ForeignKey(
                        name: "FK_skill_active_versions_skill_versions_InstallationId_SkillId_Version",
                        columns: x => new { x.InstallationId, x.SkillId, x.Version },
                        principalTable: "skill_versions",
                        principalColumns: new[] { "InstallationId", "SkillId", "Version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_skill_active_versions_InstallationId_SkillId_Version",
                table: "skill_active_versions",
                columns: new[] { "InstallationId", "SkillId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_skill_proposal_snapshots_InstallationId_SkillId_UpdatedAtUtcTicks",
                table: "skill_proposal_snapshots",
                columns: new[] { "InstallationId", "SkillId", "UpdatedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_skill_run_snapshots_InstallationId_IdempotencyKey",
                table: "skill_run_snapshots",
                columns: new[] { "InstallationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_skill_versions_ArtifactContentHash",
                table: "skill_versions",
                column: "ArtifactContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_skill_versions_InstallationId_SkillId_Status",
                table: "skill_versions",
                columns: new[] { "InstallationId", "SkillId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "skill_active_versions");

            migrationBuilder.DropTable(
                name: "skill_proposal_snapshots");

            migrationBuilder.DropTable(
                name: "skill_run_snapshots");

            migrationBuilder.DropTable(
                name: "skill_versions");
        }
    }
}
