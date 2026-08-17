using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HttpApiProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "http_api_profiles",
                columns: table => new
                {
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BaseEndpoint = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ProbeRelativePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    StaticHeadersJson = table.Column<string>(type: "TEXT", maxLength: 32768, nullable: false),
                    SecretStore = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SecretKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_http_api_profiles", x => new { x.InstallationId, x.ProfileId });
                    table.ForeignKey(
                        name: "FK_http_api_profiles_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "http_api_profiles");
        }
    }
}
