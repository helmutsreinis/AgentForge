using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableDecoderGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "decoder_active_versions",
                columns: table => new
                {
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DecoderId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CandidateHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decoder_active_versions", x => new { x.InstallationId, x.DecoderId });
                    table.ForeignKey(
                        name: "FK_decoder_active_versions_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "decoder_proposal_snapshots",
                columns: table => new
                {
                    ProposalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DecoderId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CandidateHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    BaselineHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: true),
                    PreviousSnapshotHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SnapshotHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decoder_proposal_snapshots", x => new { x.ProposalId, x.Version });
                    table.ForeignKey(
                        name: "FK_decoder_proposal_snapshots_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_decoder_proposal_snapshots_InstallationId_DecoderId_UpdatedAtUtcTicks",
                table: "decoder_proposal_snapshots",
                columns: new[] { "InstallationId", "DecoderId", "UpdatedAtUtcTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "decoder_active_versions");

            migrationBuilder.DropTable(
                name: "decoder_proposal_snapshots");
        }
    }
}
