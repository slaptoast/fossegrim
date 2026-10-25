using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace fossegrim.lib.Migrations
{
    /// <inheritdoc />
    public partial class ConvertPlaylistItemPositionToFractional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "Position",
                table: "PlaylistItems",
                type: "REAL",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "Position",
                table: "PlaylistItems",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "REAL");
        }
    }
}
