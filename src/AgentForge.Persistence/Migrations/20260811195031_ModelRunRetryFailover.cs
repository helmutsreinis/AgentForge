using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModelRunRetryFailover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsumedWallClockSeconds",
                table: "model_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaximumAttempts",
                table: "model_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ReservedEvents",
                table: "model_run_attempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ReservedInputTokens",
                table: "model_run_attempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ReservedOutputTokens",
                table: "model_run_attempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "ReservedToolCalls",
                table: "model_run_attempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ReservedWallClockSeconds",
                table: "model_run_attempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE model_run_attempts
                SET ReservedInputTokens = (
                        SELECT ReservedInputTokens FROM model_runs WHERE model_runs.Id = model_run_attempts.RunId),
                    ReservedOutputTokens = (
                        SELECT ReservedOutputTokens FROM model_runs WHERE model_runs.Id = model_run_attempts.RunId),
                    ReservedToolCalls = (
                        SELECT ReservedToolCalls FROM model_runs WHERE model_runs.Id = model_run_attempts.RunId),
                    ReservedEvents = (
                        SELECT ReservedEvents FROM model_runs WHERE model_runs.Id = model_run_attempts.RunId),
                    ReservedWallClockSeconds = (
                        SELECT ReservedWallClockSeconds FROM model_runs WHERE model_runs.Id = model_run_attempts.RunId);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsumedWallClockSeconds",
                table: "model_runs");

            migrationBuilder.DropColumn(
                name: "MaximumAttempts",
                table: "model_runs");

            migrationBuilder.DropColumn(
                name: "ReservedEvents",
                table: "model_run_attempts");

            migrationBuilder.DropColumn(
                name: "ReservedInputTokens",
                table: "model_run_attempts");

            migrationBuilder.DropColumn(
                name: "ReservedOutputTokens",
                table: "model_run_attempts");

            migrationBuilder.DropColumn(
                name: "ReservedToolCalls",
                table: "model_run_attempts");

            migrationBuilder.DropColumn(
                name: "ReservedWallClockSeconds",
                table: "model_run_attempts");
        }
    }
}
