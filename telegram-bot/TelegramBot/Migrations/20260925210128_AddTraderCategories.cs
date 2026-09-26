using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddTraderCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SellsOnlyAfterBuy",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "CategorizedAt",
                table: "Traders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Traders",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsManuallyRecategorized",
                table: "Traders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CallOutcomes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TraderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    NetworkId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenAddress = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CalledAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CallPrice = table.Column<double>(type: "REAL", nullable: true),
                    McapAtCall = table.Column<double>(type: "REAL", nullable: true),
                    SoldIn10mFrac = table.Column<double>(type: "REAL", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    EvaluatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    EntryVsCall = table.Column<double>(type: "REAL", nullable: true),
                    R1h = table.Column<double>(type: "REAL", nullable: true),
                    R24h = table.Column<double>(type: "REAL", nullable: true),
                    Pk15m = table.Column<double>(type: "REAL", nullable: true),
                    Pk1h = table.Column<double>(type: "REAL", nullable: true),
                    Pk24h = table.Column<double>(type: "REAL", nullable: true),
                    MinutesToPeak = table.Column<double>(type: "REAL", nullable: true),
                    Sl30 = table.Column<bool>(type: "INTEGER", nullable: true),
                    EvScalp = table.Column<double>(type: "REAL", nullable: true),
                    EvFlip = table.Column<double>(type: "REAL", nullable: true),
                    EvRunner = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CallOutcomes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CallOutcomes_Traders_TraderId",
                        column: x => x.TraderId,
                        principalTable: "Traders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TraderRatings",
                columns: table => new
                {
                    TraderId = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoCategory = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Basis = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Score = table.Column<double>(type: "REAL", nullable: true),
                    Cut = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Calls = table.Column<int>(type: "INTEGER", nullable: false),
                    Simulated = table.Column<int>(type: "INTEGER", nullable: false),
                    AlertsPerDay = table.Column<double>(type: "REAL", nullable: false),
                    StatsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraderRatings", x => x.TraderId);
                    table.ForeignKey(
                        name: "FK_TraderRatings_Traders_TraderId",
                        column: x => x.TraderId,
                        principalTable: "Traders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserCategoryFollows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    FollowedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCategoryFollows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserCategoryFollows_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Traders_Category",
                table: "Traders",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_CallOutcomes_CalledAt",
                table: "CallOutcomes",
                column: "CalledAt");

            migrationBuilder.CreateIndex(
                name: "IX_CallOutcomes_Status_CalledAt",
                table: "CallOutcomes",
                columns: new[] { "Status", "CalledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CallOutcomes_TraderId_Kind_NetworkId_TokenAddress",
                table: "CallOutcomes",
                columns: new[] { "TraderId", "Kind", "NetworkId", "TokenAddress" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserCategoryFollows_Category",
                table: "UserCategoryFollows",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_UserCategoryFollows_UserId_Category",
                table: "UserCategoryFollows",
                columns: new[] { "UserId", "Category" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CallOutcomes");

            migrationBuilder.DropTable(
                name: "TraderRatings");

            migrationBuilder.DropTable(
                name: "UserCategoryFollows");

            migrationBuilder.DropIndex(
                name: "IX_Traders_Category",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "SellsOnlyAfterBuy",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CategorizedAt",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Traders");

            migrationBuilder.DropColumn(
                name: "IsManuallyRecategorized",
                table: "Traders");
        }
    }
}
