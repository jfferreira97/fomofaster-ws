using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddReferralLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CommissionOnRenewals",
                table: "ReferralCodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "CommissionPercent",
                table: "ReferralCodes",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "ReferralPayouts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReferralCodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AmountSol = table.Column<decimal>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferralPayouts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReferralPayouts_ReferralCodeId",
                table: "ReferralPayouts",
                column: "ReferralCodeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReferralPayouts");

            migrationBuilder.DropColumn(
                name: "CommissionOnRenewals",
                table: "ReferralCodes");

            migrationBuilder.DropColumn(
                name: "CommissionPercent",
                table: "ReferralCodes");
        }
    }
}
