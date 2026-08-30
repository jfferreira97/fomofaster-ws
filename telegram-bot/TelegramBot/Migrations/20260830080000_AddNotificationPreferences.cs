using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TelegramBot.Data;

#nullable disable

namespace TelegramBot.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260830080000_AddNotificationPreferences")]
    public partial class AddNotificationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Formerly the single global auto-follow switch — renamed, not replaced, so
            // existing users' preference carries over as their FOMO-specific setting.
            migrationBuilder.RenameColumn(
                name: "AutoFollowNewTraders",
                table: "Users",
                newName: "AutoFollowFomoTraders");

            migrationBuilder.AddColumn<bool>(
                name: "AutoFollowPumpTraders",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyFomoBuySell",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyFomoThesis",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyPumpCallouts",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PumpVerifiedOnly",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyTrending",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "AutoFollowPumpTraders", table: "Users");
            migrationBuilder.DropColumn(name: "NotifyFomoBuySell", table: "Users");
            migrationBuilder.DropColumn(name: "NotifyFomoThesis", table: "Users");
            migrationBuilder.DropColumn(name: "NotifyPumpCallouts", table: "Users");
            migrationBuilder.DropColumn(name: "PumpVerifiedOnly", table: "Users");
            migrationBuilder.DropColumn(name: "NotifyTrending", table: "Users");

            migrationBuilder.RenameColumn(
                name: "AutoFollowFomoTraders",
                table: "Users",
                newName: "AutoFollowNewTraders");
        }
    }
}
