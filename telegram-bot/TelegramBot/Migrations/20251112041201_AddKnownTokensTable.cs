using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddKnownTokensTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnownTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ContractAddress = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MinMarketCap = table.Column<long>(type: "INTEGER", nullable: false),
                    Chain = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnownTokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnownTokens_Symbol",
                table: "KnownTokens",
                column: "Symbol");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnownTokens");
        }
    }
}
