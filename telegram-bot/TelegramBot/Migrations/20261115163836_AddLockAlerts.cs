using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddLockAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LockMaxMarketCap",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LockMaxTokenAgeHours",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LockMinDays",
                table: "Users",
                type: "INTEGER",
                nullable: true,
                defaultValue: 7);   // existing users start with the same limits as new ones (User.cs)

            migrationBuilder.AddColumn<decimal>(
                name: "LockMinMarketCap",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LockMinPercent",
                table: "Users",
                type: "TEXT",
                nullable: true,
                defaultValue: 1m);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyLocks",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);   // lock alerts are on by default, like trending

            migrationBuilder.CreateTable(
                name: "LockEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Signature = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Mint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    StreamAccount = table.Column<string>(type: "TEXT", nullable: false),
                    Sender = table.Column<string>(type: "TEXT", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(38,9)", nullable: false),
                    PercentSupply = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    UsdValue = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    MarketCapUsd = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    StartAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CliffAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PairCreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SkipReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LockEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LockEvents_Mint",
                table: "LockEvents",
                column: "Mint");

            migrationBuilder.CreateIndex(
                name: "IX_LockEvents_Signature",
                table: "LockEvents",
                column: "Signature",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LockEvents");

            migrationBuilder.DropColumn(
                name: "LockMaxMarketCap",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LockMaxTokenAgeHours",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LockMinDays",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LockMinMarketCap",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LockMinPercent",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "NotifyLocks",
                table: "Users");
        }
    }
}
