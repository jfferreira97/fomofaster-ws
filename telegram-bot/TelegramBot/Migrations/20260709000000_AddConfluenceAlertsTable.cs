using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TelegramBot.Data;

#nullable disable

namespace TelegramBot.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260709000000_AddConfluenceAlertsTable")]
    public partial class AddConfluenceAlertsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfluenceAlerts",
                columns: table => new
                {
                    Id              = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TokenAddress    = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Ticker          = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    NetworkId       = table.Column<int>(type: "INTEGER", nullable: true),
                    WindowStartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TraderCount     = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalUsd        = table.Column<decimal>(type: "TEXT", nullable: false),
                    FiredAt         = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfluenceAlerts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfluenceAlerts_TokenAddress_FiredAt",
                table: "ConfluenceAlerts",
                columns: new[] { "TokenAddress", "FiredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ConfluenceAlerts");
        }
    }
}
