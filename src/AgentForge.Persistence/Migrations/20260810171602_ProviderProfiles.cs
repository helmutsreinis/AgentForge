using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProviderProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProviderType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SecretStore = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SecretKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    TextGeneration = table.Column<bool>(type: "INTEGER", nullable: false),
                    Streaming = table.Column<bool>(type: "INTEGER", nullable: false),
                    ToolCalls = table.Column<bool>(type: "INTEGER", nullable: false),
                    Images = table.Column<bool>(type: "INTEGER", nullable: false),
                    EvidenceSource = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provider_profiles_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_profiles_InstallationId_Name",
                table: "provider_profiles",
                columns: new[] { "InstallationId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_profiles");
        }
    }
}
