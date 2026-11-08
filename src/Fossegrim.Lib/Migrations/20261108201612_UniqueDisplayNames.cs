using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace fossegrim.lib.Migrations
{
    /// <inheritdoc />
    public partial class UniqueDisplayNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedDisplayName",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_NormalizedDisplayName",
                table: "AspNetUsers",
                column: "NormalizedDisplayName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_NormalizedDisplayName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "NormalizedDisplayName",
                table: "AspNetUsers");
        }
    }
}
