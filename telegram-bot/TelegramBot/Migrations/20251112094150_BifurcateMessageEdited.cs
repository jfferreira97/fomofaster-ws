using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class BifurcateMessageEdited : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsEdited",
                table: "SentMessages",
                newName: "IsSystemEdited");

            migrationBuilder.AddColumn<bool>(
                name: "IsManuallyEdited",
                table: "SentMessages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsManuallyEdited",
                table: "SentMessages");

            migrationBuilder.RenameColumn(
                name: "IsSystemEdited",
                table: "SentMessages",
                newName: "IsEdited");
        }
    }
}
