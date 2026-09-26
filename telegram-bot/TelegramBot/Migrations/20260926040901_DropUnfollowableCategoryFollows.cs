using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramBot.Migrations
{
    /// <inheritdoc />
    public partial class DropUnfollowableCategoryFollows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // −EV and Unrated can't be followed as a group any more
            migrationBuilder.Sql("DELETE FROM \"UserCategoryFollows\" WHERE \"Category\" IN ('noise','unrated')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
