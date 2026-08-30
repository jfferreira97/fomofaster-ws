using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformToTraderAndNotification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Traders_Handle",
                table: "Traders");

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "Traders",
                type: "TEXT",
                nullable: false,
                defaultValue: "Fomo");

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "Notifications",
                type: "TEXT",
                nullable: false,
                defaultValue: "Fomo");

            migrationBuilder.CreateIndex(
                name: "IX_Traders_Handle_Platform",
                table: "Traders",
                columns: new[] { "Handle", "Platform" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Traders_Handle_Platform",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "Notifications");

            migrationBuilder.CreateIndex(
                name: "IX_Traders_Handle",
                table: "Traders",
                column: "Handle",
                unique: true);
        }
    }
}
