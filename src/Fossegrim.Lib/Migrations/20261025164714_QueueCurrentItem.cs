using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace fossegrim.lib.Migrations
{
    /// <inheritdoc />
    public partial class QueueCurrentItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Playlists_MediaItems_CurrentMediaItemId",
                table: "Playlists");

            migrationBuilder.RenameColumn(
                name: "CurrentMediaItemId",
                table: "Playlists",
                newName: "CurrentItemId");

            migrationBuilder.RenameIndex(
                name: "IX_Playlists_CurrentMediaItemId",
                table: "Playlists",
                newName: "IX_Playlists_CurrentItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to a track id (QueueCurrentItemForeignKey's Down has already
            // removed the foreign key to PlaylistItems).
            migrationBuilder.Sql("""
                UPDATE "Playlists"
                SET "CurrentItemId" = (
                    SELECT pi."MediaItemId" FROM "PlaylistItems" pi WHERE pi."Id" = "Playlists"."CurrentItemId")
                WHERE "CurrentItemId" IS NOT NULL;
                """);

            migrationBuilder.RenameColumn(
                name: "CurrentItemId",
                table: "Playlists",
                newName: "CurrentMediaItemId");

            migrationBuilder.RenameIndex(
                name: "IX_Playlists_CurrentItemId",
                table: "Playlists",
                newName: "IX_Playlists_CurrentMediaItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_Playlists_MediaItems_CurrentMediaItemId",
                table: "Playlists",
                column: "CurrentMediaItemId",
                principalTable: "MediaItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
