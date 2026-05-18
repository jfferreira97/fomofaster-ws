using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TelegramBot.Data;

#nullable disable

namespace TelegramBot.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260519000000_DropFomoWsTradeIdUniqueIndex")]
    public partial class DropFomoWsTradeIdUniqueIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Notifications_FomoWsTradeId\"");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_Notifications_FomoWsTradeId\" ON \"Notifications\" (\"FomoWsTradeId\") WHERE \"FomoWsTradeId\" IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Notifications_FomoWsTradeId\"");
            migrationBuilder.Sql("CREATE UNIQUE INDEX \"IX_Notifications_FomoWsTradeId\" ON \"Notifications\" (\"FomoWsTradeId\") WHERE \"FomoWsTradeId\" IS NOT NULL");
        }
    }
}
