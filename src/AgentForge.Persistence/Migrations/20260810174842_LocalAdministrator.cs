using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LocalAdministrator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "local_administrators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SecretStore = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SecretKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    VerifierAlgorithm = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    VerifierWorkFactor = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifierSalt = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Verifier = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_administrators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_local_administrators_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_local_administrators_ActorId",
                table: "local_administrators",
                column: "ActorId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_local_administrators_InstallationId",
                table: "local_administrators",
                column: "InstallationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "local_administrators");
        }
    }
}
