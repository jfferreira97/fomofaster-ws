using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TelegramBot.Data;

#nullable disable

namespace TelegramBot.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260518130000_DropFomoWsJson")]
    public partial class DropFomoWsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Notifications\" DROP COLUMN \"FomoWsJson\"");
            migrationBuilder.DropTable(name: "KnownTokens");
            migrationBuilder.DropTable(name: "CachedTokenAddresses");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FomoWsJson",
                table: "Notifications",
                type: "TEXT",
                nullable: true);
        }
    }
}
