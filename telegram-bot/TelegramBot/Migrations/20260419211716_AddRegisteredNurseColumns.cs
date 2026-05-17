using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddRegisteredNurseColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRN4L",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsRegisteredNurse",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "RNExpiresAt",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PendingPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    WalletPublicKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    WalletPrivateKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AmountSol = table.Column<decimal>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsConfirmed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingPayments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingPayments_IsConfirmed_ExpiresAt",
                table: "PendingPayments",
                columns: new[] { "IsConfirmed", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingPayments_WalletPublicKey",
                table: "PendingPayments",
                column: "WalletPublicKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingPayments");

            migrationBuilder.DropColumn(
                name: "IsRN4L",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsRegisteredNurse",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RNExpiresAt",
                table: "Users");
        }
    }
}
