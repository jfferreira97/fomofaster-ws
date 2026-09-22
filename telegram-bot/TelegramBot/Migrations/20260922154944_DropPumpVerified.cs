using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class DropPumpVerified : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PumpVerifiedOnly",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsPumpVerified",
                table: "Traders");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PumpVerifiedOnly",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPumpVerified",
                table: "Traders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
