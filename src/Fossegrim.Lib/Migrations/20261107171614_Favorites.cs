using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace fossegrim.lib.Migrations
{
    /// <inheritdoc />
    public partial class Favorites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FavoriteAlbums",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    AlbumId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FavoriteAlbums", x => new { x.UserId, x.AlbumId });
                    table.ForeignKey(
                        name: "FK_FavoriteAlbums_Albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "Albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FavoriteAlbums_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FavoriteArtists",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ArtistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FavoriteArtists", x => new { x.UserId, x.ArtistId });
                    table.ForeignKey(
                        name: "FK_FavoriteArtists_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FavoriteArtists_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FavoriteTracks",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FavoriteTracks", x => new { x.UserId, x.MediaItemId });
                    table.ForeignKey(
                        name: "FK_FavoriteTracks_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FavoriteTracks_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FavoriteAlbums_AlbumId",
                table: "FavoriteAlbums",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "IX_FavoriteAlbums_UserId_CreatedAt",
                table: "FavoriteAlbums",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FavoriteArtists_ArtistId",
                table: "FavoriteArtists",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_FavoriteArtists_UserId_CreatedAt",
                table: "FavoriteArtists",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FavoriteTracks_MediaItemId",
                table: "FavoriteTracks",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_FavoriteTracks_UserId_CreatedAt",
                table: "FavoriteTracks",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FavoriteAlbums");

            migrationBuilder.DropTable(
                name: "FavoriteArtists");

            migrationBuilder.DropTable(
                name: "FavoriteTracks");
        }
    }
}
