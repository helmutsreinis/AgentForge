using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderedEventOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_ProcessedAt_OccurredAt",
                table: "outbox_messages");

            migrationBuilder.AddColumn<long>(
                name: "OccurredAtUtcTicks",
                table: "outbox_messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_ProcessedAt_OccurredAtUtcTicks_Id",
                table: "outbox_messages",
                columns: new[] { "ProcessedAt", "OccurredAtUtcTicks", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_ProcessedAt_OccurredAtUtcTicks_Id",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "OccurredAtUtcTicks",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_ProcessedAt_OccurredAt",
                table: "outbox_messages",
                columns: new[] { "ProcessedAt", "OccurredAt" });
        }
    }
}
