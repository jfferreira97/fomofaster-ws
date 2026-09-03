using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddThresholdAndFollowExclusion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MinValueUsd",
                table: "UserTraders",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TraderFollowExclusions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    TraderId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExcludedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraderFollowExclusions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TraderFollowExclusions_Traders_TraderId",
                        column: x => x.TraderId,
                        principalTable: "Traders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TraderFollowExclusions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TraderFollowExclusions_TraderId",
                table: "TraderFollowExclusions",
                column: "TraderId");

            migrationBuilder.CreateIndex(
                name: "IX_TraderFollowExclusions_UserId_TraderId",
                table: "TraderFollowExclusions",
                columns: new[] { "UserId", "TraderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TraderFollowExclusions");

            migrationBuilder.DropColumn(
                name: "MinValueUsd",
                table: "UserTraders");
        }
    }
}
