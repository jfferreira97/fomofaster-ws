using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifyPumpBuySell : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NotifyPumpBuySell",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Anyone who already gets pump activity (callouts) gets pump transactions by default.
            migrationBuilder.Sql("UPDATE \"Users\" SET \"NotifyPumpBuySell\" = \"NotifyPumpCallouts\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotifyPumpBuySell",
                table: "Users");
        }
    }
}
