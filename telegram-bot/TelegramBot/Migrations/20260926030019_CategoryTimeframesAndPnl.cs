using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class CategoryTimeframesAndPnl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OwnClosedPositions",
                table: "Traders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "OwnRealizedUsd",
                table: "Traders",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Pnl30dUsd",
                table: "Traders",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Pnl7dUsd",
                table: "Traders",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PnlRank30d",
                table: "Traders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PnlUpdatedAt",
                table: "Traders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PortfolioPnlUsd",
                table: "Traders",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PumpUserId",
                table: "Traders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Wallet",
                table: "Traders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EvQuick",
                table: "CallOutcomes",
                type: "REAL",
                nullable: true);

            // Categories are now take-profit windows; the market-cap ones (swing, degen) are gone.
            // Their follows go, and their traders go back to the categorizer, even hand-placed ones.
            migrationBuilder.Sql("DELETE FROM \"UserCategoryFollows\" WHERE \"Category\" IN ('swing','degen')");
            migrationBuilder.Sql("UPDATE \"Traders\" SET \"Category\" = NULL, \"IsManuallyRecategorized\" = 0 WHERE \"Category\" IN ('swing','degen')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnClosedPositions",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "OwnRealizedUsd",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "Pnl30dUsd",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "Pnl7dUsd",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "PnlRank30d",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "PnlUpdatedAt",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "PortfolioPnlUsd",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "PumpUserId",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "Wallet",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "EvQuick",
                table: "CallOutcomes");
        }
    }
}
