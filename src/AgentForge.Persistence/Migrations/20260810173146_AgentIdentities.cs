using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_identities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "NOCASE"),
                    Expertise = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Mission = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    PreferredLanguage = table.Column<string>(type: "TEXT", maxLength: 35, nullable: false),
                    TimeZone = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ResponseStyle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    DefaultWorkspace = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    PrimaryProviderProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DataLocality = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AllowFallback = table.Column<bool>(type: "INTEGER", nullable: false),
                    MemoryScope = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MemoryRetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    NetworkPosture = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ToolGrantsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SkillGrantsJson = table.Column<string>(type: "TEXT", nullable: false),
                    MaxTurns = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxToolInvocations = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxInputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxOutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxWallClockSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxChildDepth = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxChildren = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxChildConcurrency = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxChildTotalTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    LearningMode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MutableSkillScope = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_identities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_identities_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_identities_provider_profiles_PrimaryProviderProfileId",
                        column: x => x.PrimaryProviderProfileId,
                        principalTable: "provider_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_identities_InstallationId_Name",
                table: "agent_identities",
                columns: new[] { "InstallationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_identities_PrimaryProviderProfileId",
                table: "agent_identities",
                column: "PrimaryProviderProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_identities");
        }
    }
}
