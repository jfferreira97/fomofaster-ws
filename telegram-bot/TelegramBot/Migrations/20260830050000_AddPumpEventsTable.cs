using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TelegramBot.Data;

#nullable disable

namespace TelegramBot.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260830050000_AddPumpEventsTable")]
    public partial class AddPumpEventsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PumpEvents",
                columns: table => new
                {
                    Id          = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExternalId  = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind        = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ActorUserId = table.Column<string>(type: "TEXT", nullable: true),
                    ActorHandle = table.Column<string>(type: "TEXT", nullable: true),
                    CoinMint    = table.Column<string>(type: "TEXT", nullable: true),
                    ChainId     = table.Column<int>(type: "INTEGER", nullable: true),
                    Symbol      = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt   = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReceivedAt  = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RawJson     = table.Column<string>(type: "TEXT", nullable: false),
                    Handled     = table.Column<bool>(type: "INTEGER", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PumpEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PumpEvents_ExternalId",
                table: "PumpEvents",
                column: "ExternalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PumpEvents_Kind",
                table: "PumpEvents",
                column: "Kind");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PumpEvents");
        }
    }
}
