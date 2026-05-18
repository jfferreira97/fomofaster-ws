using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TelegramBot.Data;

#nullable disable

namespace TelegramBot.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260518150000_AddWsEventsHandled")]
    public partial class AddWsEventsHandled : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"WsEvents\" ADD COLUMN \"Handled\" INTEGER NOT NULL DEFAULT 0");
            migrationBuilder.CreateIndex(name: "IX_WsEvents_Handled", table: "WsEvents", column: "Handled");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_WsEvents_Handled", table: "WsEvents");
        }
    }
}
