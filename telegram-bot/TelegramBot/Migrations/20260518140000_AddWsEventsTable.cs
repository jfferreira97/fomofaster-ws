using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TelegramBot.Data;

#nullable disable

namespace TelegramBot.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260518140000_AddWsEventsTable")]
    public partial class AddWsEventsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WsEvents",
                columns: table => new
                {
                    Id                 = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WsId               = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Type               = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    UserId             = table.Column<string>(type: "TEXT", nullable: true),
                    UserHandle         = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayName        = table.Column<string>(type: "TEXT", nullable: true),
                    TradeId            = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TokenAddress       = table.Column<string>(type: "TEXT", nullable: true),
                    NetworkId          = table.Column<int>(type: "INTEGER", nullable: true),
                    Ticker             = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt          = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReceivedAt         = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Equity             = table.Column<decimal>(type: "TEXT", nullable: true),
                    Price              = table.Column<decimal>(type: "TEXT", nullable: true),
                    MarketCap          = table.Column<decimal>(type: "TEXT", nullable: true),
                    UsdAmount          = table.Column<decimal>(type: "TEXT", nullable: true),
                    Tag                = table.Column<string>(type: "TEXT", nullable: true),
                    TotalCostBasis     = table.Column<decimal>(type: "TEXT", nullable: true),
                    TotalPnlUsd        = table.Column<decimal>(type: "TEXT", nullable: true),
                    TotalPercentagePnl = table.Column<decimal>(type: "TEXT", nullable: true),
                    EntryTime          = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ShowAbsolutePnl    = table.Column<bool>(type: "INTEGER", nullable: true),
                    RawJson            = table.Column<string>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WsEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WsEvents_WsId",
                table: "WsEvents",
                column: "WsId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WsEvents_Type",
                table: "WsEvents",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_WsEvents_TradeId",
                table: "WsEvents",
                column: "TradeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "WsEvents");
        }
    }
}
