using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationLookupTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContractAddressSource",
                table: "Notifications",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "LookupDuration",
                table: "Notifications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MarketCapAtNotification",
                table: "Notifications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimesCacheHit",
                table: "Notifications",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TimesDexScreenerApiHit",
                table: "Notifications",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TimesHeliusApiHit",
                table: "Notifications",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "WasRetried",
                table: "Notifications",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContractAddressSource",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "LookupDuration",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "MarketCapAtNotification",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "TimesCacheHit",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "TimesDexScreenerApiHit",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "TimesHeliusApiHit",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "WasRetried",
                table: "Notifications");
        }
    }
}
