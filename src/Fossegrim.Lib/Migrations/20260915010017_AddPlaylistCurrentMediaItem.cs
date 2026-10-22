using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace fossegrim.lib.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylistCurrentMediaItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CurrentMediaItemId",
                table: "Playlists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Playlists_CurrentMediaItemId",
                table: "Playlists",
                column: "CurrentMediaItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_Playlists_MediaItems_CurrentMediaItemId",
                table: "Playlists",
                column: "CurrentMediaItemId",
                principalTable: "MediaItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Playlists_MediaItems_CurrentMediaItemId",
                table: "Playlists");

            migrationBuilder.DropIndex(
                name: "IX_Playlists_CurrentMediaItemId",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "CurrentMediaItemId",
                table: "Playlists");
        }
    }
}
