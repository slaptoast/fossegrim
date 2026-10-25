using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace fossegrim.lib.Migrations
{
    /// <inheritdoc />
    public partial class ManyToManyArtistMediaItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaItems_Artists_ArtistId",
                table: "MediaItems");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_ArtistId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "ArtistId",
                table: "MediaItems");

            migrationBuilder.RenameColumn(
                name: "title",
                table: "MediaItems",
                newName: "Title");

            migrationBuilder.RenameColumn(
                name: "fileLocation",
                table: "MediaItems",
                newName: "FileLocation");

            migrationBuilder.RenameColumn(
                name: "album",
                table: "MediaItems",
                newName: "Album");

            migrationBuilder.RenameColumn(
                name: "artist",
                table: "MediaItems",
                newName: "ArtistName");

            migrationBuilder.RenameIndex(
                name: "IX_MediaItems_title",
                table: "MediaItems",
                newName: "IX_MediaItems_Title");

            migrationBuilder.RenameIndex(
                name: "IX_MediaItems_fileLocation",
                table: "MediaItems",
                newName: "IX_MediaItems_FileLocation");

            migrationBuilder.RenameIndex(
                name: "IX_MediaItems_album",
                table: "MediaItems",
                newName: "IX_MediaItems_Album");

            migrationBuilder.RenameIndex(
                name: "IX_MediaItems_artist",
                table: "MediaItems",
                newName: "IX_MediaItems_ArtistName");

            migrationBuilder.CreateTable(
                name: "MediaItemArtists",
                columns: table => new
                {
                    ArtistsId = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaItemsId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaItemArtists", x => new { x.ArtistsId, x.MediaItemsId });
                    table.ForeignKey(
                        name: "FK_MediaItemArtists_Artists_ArtistsId",
                        column: x => x.ArtistsId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MediaItemArtists_MediaItems_MediaItemsId",
                        column: x => x.MediaItemsId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItemArtists_MediaItemsId",
                table: "MediaItemArtists",
                column: "MediaItemsId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaItemArtists");

            migrationBuilder.RenameColumn(
                name: "Title",
                table: "MediaItems",
                newName: "title");

            migrationBuilder.RenameColumn(
                name: "FileLocation",
                table: "MediaItems",
                newName: "fileLocation");

            migrationBuilder.RenameColumn(
                name: "Album",
                table: "MediaItems",
                newName: "album");

            migrationBuilder.RenameColumn(
                name: "ArtistName",
                table: "MediaItems",
                newName: "artist");

            migrationBuilder.RenameIndex(
                name: "IX_MediaItems_Title",
                table: "MediaItems",
                newName: "IX_MediaItems_title");

            migrationBuilder.RenameIndex(
                name: "IX_MediaItems_FileLocation",
                table: "MediaItems",
                newName: "IX_MediaItems_fileLocation");

            migrationBuilder.RenameIndex(
                name: "IX_MediaItems_Album",
                table: "MediaItems",
                newName: "IX_MediaItems_album");

            migrationBuilder.RenameIndex(
                name: "IX_MediaItems_ArtistName",
                table: "MediaItems",
                newName: "IX_MediaItems_artist");

            migrationBuilder.AddColumn<int>(
                name: "ArtistId",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_ArtistId",
                table: "MediaItems",
                column: "ArtistId");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaItems_Artists_ArtistId",
                table: "MediaItems",
                column: "ArtistId",
                principalTable: "Artists",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
