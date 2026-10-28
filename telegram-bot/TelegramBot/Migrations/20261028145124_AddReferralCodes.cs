using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddReferralCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReferredAt",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferredByCode",
                table: "Users",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferralCode",
                table: "PendingPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReferralCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    DiscountPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    StatsToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferralCodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_ReferredByCode",
                table: "Users",
                column: "ReferredByCode",
                filter: "\"ReferredByCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralCodes_Code",
                table: "ReferralCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferralCodes_StatsToken",
                table: "ReferralCodes",
                column: "StatsToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReferralCodes");

            migrationBuilder.DropIndex(
                name: "IX_Users_ReferredByCode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReferredAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReferredByCode",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReferralCode",
                table: "PendingPayments");
        }
    }
}
