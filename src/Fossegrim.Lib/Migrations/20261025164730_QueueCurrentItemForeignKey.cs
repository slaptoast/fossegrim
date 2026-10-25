using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace fossegrim.lib.Migrations
{
    /// <inheritdoc />
    public partial class QueueCurrentItemForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // QueueCurrentItem renamed the column but it still holds a track
            // (MediaItem) id. Point it at that track's first entry in the same
            // queue instead, or clear it if there's none. This has to happen
            // before the foreign key below exists, which is why it's a separate
            // migration: SQLite applies foreign keys by rebuilding the table at
            // the end of a migration.
            migrationBuilder.Sql("""
                UPDATE "Playlists"
                SET "CurrentItemId" = (
                    SELECT pi."Id" FROM "PlaylistItems" pi
                    WHERE pi."PlaylistId" = "Playlists"."Id" AND pi."MediaItemId" = "Playlists"."CurrentItemId"
                    ORDER BY pi."Position"
                    LIMIT 1)
                WHERE "CurrentItemId" IS NOT NULL;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_Playlists_PlaylistItems_CurrentItemId",
                table: "Playlists",
                column: "CurrentItemId",
                principalTable: "PlaylistItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Playlists_PlaylistItems_CurrentItemId",
                table: "Playlists");
        }
    }
}
