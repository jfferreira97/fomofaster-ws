using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class CleanNotificationsAndDropDeadTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_TradeId",
                table: "Notifications");

            migrationBuilder.RenameColumn(
                name: "TradeId",
                table: "Notifications",
                newName: "FomoWsTradeId");

            migrationBuilder.AddColumn<string>(
                name: "FomoWsJson",
                table: "Notifications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_FomoWsTradeId",
                table: "Notifications",
                column: "FomoWsTradeId",
                unique: true,
                filter: "\"FomoWsTradeId\" IS NOT NULL");

            migrationBuilder.DropColumn(name: "HasCA",                    table: "Notifications");
            migrationBuilder.DropColumn(name: "ContractAddressSource",    table: "Notifications");
            migrationBuilder.DropColumn(name: "LookupDuration",           table: "Notifications");
            migrationBuilder.DropColumn(name: "TimesCacheHit",            table: "Notifications");
            migrationBuilder.DropColumn(name: "TimesDexScreenerApiHit",   table: "Notifications");
            migrationBuilder.DropColumn(name: "TimesHeliusApiHit",        table: "Notifications");
            migrationBuilder.DropColumn(name: "WasRetried",               table: "Notifications");
            migrationBuilder.DropColumn(name: "LookupDiagnostics",        table: "Notifications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_FomoWsTradeId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "FomoWsJson",
                table: "Notifications");

            migrationBuilder.RenameColumn(
                name: "FomoWsTradeId",
                table: "Notifications",
                newName: "TradeId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TradeId",
                table: "Notifications",
                column: "TradeId",
                unique: true,
                filter: "\"TradeId\" IS NOT NULL");
        }
    }
}
