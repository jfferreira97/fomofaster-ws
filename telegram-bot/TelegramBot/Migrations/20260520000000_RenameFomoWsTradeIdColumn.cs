using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using TelegramBot.Data;

#nullable disable

namespace TelegramBot.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260520000000_RenameFomoWsTradeIdColumn")]
    public partial class RenameFomoWsTradeIdColumn : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Notifications_FomoWsTradeId\"");
            migrationBuilder.Sql("ALTER TABLE \"Notifications\" RENAME COLUMN \"FomoWsTradeId\" TO \"FK_WsEvent_WsId\"");
            migrationBuilder.Sql("CREATE INDEX \"IX_Notifications_FK_WsEvent_WsId\" ON \"Notifications\" (\"FK_WsEvent_WsId\") WHERE \"FK_WsEvent_WsId\" IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Notifications_FK_WsEvent_WsId\"");
            migrationBuilder.Sql("ALTER TABLE \"Notifications\" RENAME COLUMN \"FK_WsEvent_WsId\" TO \"FomoWsTradeId\"");
            migrationBuilder.Sql("CREATE INDEX \"IX_Notifications_FomoWsTradeId\" ON \"Notifications\" (\"FomoWsTradeId\") WHERE \"FomoWsTradeId\" IS NOT NULL");
        }
    }
}
