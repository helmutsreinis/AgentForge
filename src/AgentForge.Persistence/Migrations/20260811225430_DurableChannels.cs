using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    DeliveryJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_channel_deliveries_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_channel_deliveries_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "channel_identity_bindings",
                columns: table => new
                {
                    Channel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ExternalSenderId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    EvidenceHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_identity_bindings", x => new { x.Channel, x.AccountId, x.ExternalSenderId });
                    table.ForeignKey(
                        name: "FK_channel_identity_bindings_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_channel_identity_bindings_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "channel_inbound_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ExternalMessageId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MessageHash = table.Column<string>(type: "TEXT", maxLength: 71, nullable: false),
                    OrderKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ReceivedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_inbound_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_channel_inbound_messages_agent_identities_AgentId",
                        column: x => x.AgentId,
                        principalTable: "agent_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_channel_inbound_messages_installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_channel_deliveries_AgentId",
                table: "channel_deliveries",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_channel_deliveries_InstallationId_AgentId_Channel_State_UpdatedAtUtcTicks",
                table: "channel_deliveries",
                columns: new[] { "InstallationId", "AgentId", "Channel", "State", "UpdatedAtUtcTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_channel_deliveries_InstallationId_IdempotencyKey",
                table: "channel_deliveries",
                columns: new[] { "InstallationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_channel_identity_bindings_AgentId",
                table: "channel_identity_bindings",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_channel_identity_bindings_InstallationId",
                table: "channel_identity_bindings",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_channel_inbound_messages_AgentId",
                table: "channel_inbound_messages",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_channel_inbound_messages_Channel_AccountId_ExternalMessageId",
                table: "channel_inbound_messages",
                columns: new[] { "Channel", "AccountId", "ExternalMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_channel_inbound_messages_InstallationId_AgentId_OrderKey",
                table: "channel_inbound_messages",
                columns: new[] { "InstallationId", "AgentId", "OrderKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_deliveries");

            migrationBuilder.DropTable(
                name: "channel_identity_bindings");

            migrationBuilder.DropTable(
                name: "channel_inbound_messages");
        }
    }
}
