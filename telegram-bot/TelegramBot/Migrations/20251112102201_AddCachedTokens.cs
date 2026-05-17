using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedTokenAddresses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ContractAddress = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LastAccessed = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedTokenAddresses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedTokenAddresses_ExpiresAt",
                table: "CachedTokenAddresses",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_CachedTokenAddresses_Ticker",
                table: "CachedTokenAddresses",
                column: "Ticker",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedTokenAddresses");
        }
    }
}
