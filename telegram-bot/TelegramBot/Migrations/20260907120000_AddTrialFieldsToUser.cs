using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddTrialFieldsToUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TrialExpiresAt",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TrialExpiryNotified",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Users_TrialExpiresAt",
                table: "Users",
                column: "TrialExpiresAt",
                filter: "\"TrialExpiresAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_TrialExpiresAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TrialExpiryNotified",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TrialExpiresAt",
                table: "Users");
        }
    }
}
