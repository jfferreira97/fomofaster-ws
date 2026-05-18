using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class DropHiddenColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Traders\" DROP COLUMN \"IsHidden\"");
            migrationBuilder.Sql("ALTER TABLE \"Users\" DROP COLUMN \"HasHiddenTradersAccess\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Traders\" ADD COLUMN \"IsHidden\" INTEGER NOT NULL DEFAULT 0");
            migrationBuilder.Sql("ALTER TABLE \"Users\" ADD COLUMN \"HasHiddenTradersAccess\" INTEGER NOT NULL DEFAULT 0");
        }
    }
}
